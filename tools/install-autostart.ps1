#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
    Puts every process of a deployment under Task Scheduler so it survives a reboot, a
    crash, and — the case that actually bit us — the terminal that started it closing.

.DESCRIPTION
    A process started by hand from a shell is a child of that shell's job object: close
    the window and Windows takes the whole tree down, with no crash dump, no event log
    entry and no shutdown line in the bot's own log. That is silent, and it cost us 14
    hours offline on 2026-08-13.

    Every entry is registered the same way: run at logon, then re-check every
    -CheckMinutes with MultipleInstances=IgnoreNew. While the process lives its task
    counts as running, so the repeating tick is a no-op; the moment the process dies the
    task ends and the next tick starts it again. That is the entire supervisor — no
    Windows service, no wrapper loop, nothing new to keep alive.

    The task list is read from <DeployRoot>\autostart.json, never from this repository:
    which processes a given box runs is deployment identity, and identity stays out of
    git. With no such file only the bot is registered.

    Entry shape (Execute and WorkingDirectory may be relative to -DeployRoot):

        [
          {
            "Name": "LeBot",                     // scheduled task name
            "Execute": "LeBot.Host.exe",
            "Arguments": "",                     // optional
            "WorkingDirectory": "",              // optional, defaults to -DeployRoot
            "Match": "LeBot.Host.exe",           // optional command-line substring that
                                                 // identifies an already-running copy
            "StartupShortcut": "LeBot foo.lnk"   // optional Startup shortcut this task
                                                 // replaces — disabled so it cannot
                                                 // start a second copy at logon
          }
        ]

.EXAMPLE
    pwsh tools/install-autostart.ps1
.EXAMPLE
    pwsh tools/install-autostart.ps1 -DeployRoot D:\lebot -CheckMinutes 1
.EXAMPLE
    pwsh tools/install-autostart.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Where the running deployment lives — exes, autostart.json, working directory.
    [string] $DeployRoot = 'D:\lebot',

    # How often a dead process is noticed. Costs nothing while the process is alive:
    # IgnoreNew makes the tick a no-op.
    [int] $CheckMinutes = 1,

    # Remove the tasks and restore any Startup shortcuts they replaced.
    [switch] $Uninstall,

    # Leave Startup shortcuts alone. Only do this if you want the shortcut to stay as a
    # second way in — it can start a second copy the task knows nothing about.
    [switch] $KeepStartupShortcuts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ConfigPath = Join-Path $DeployRoot 'autostart.json'
$StartupDir = [Environment]::GetFolderPath('Startup')
$UserId     = '{0}\{1}' -f $env:USERDOMAIN, $env:USERNAME

# Strict mode turns a missing property into a terminating error, which would make every
# optional field in autostart.json mandatory in practice.
function Get-Prop {
    param([object] $Object, [string] $Name, [object] $Default = $null)

    if ($null -eq $Object) { return $Default }
    if (-not ($Object.PSObject.Properties | Where-Object { $_.Name -eq $Name })) { return $Default }
    $value = $Object.$Name
    if ($value -is [string] -and [string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value
}

function Resolve-DeployPath {
    param([string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $DeployRoot $Path
}

# Same as above but falls back to PATH, so an entry can name a host interpreter
# ("pwsh.exe") as naturally as it names an exe sitting in the deployment folder. The task
# stores the resolved absolute path — PATH at logon is not guaranteed to be PATH now.
function Resolve-Executable {
    param([string] $Path)

    $resolved = Resolve-DeployPath $Path
    if (Test-Path $resolved) { return $resolved }

    $onPath = Get-Command $Path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    return $resolved
}

function Get-Entries {
    if (-not (Test-Path $ConfigPath)) {
        Write-Host "No $ConfigPath - registering the bot only."
        return @([pscustomobject]@{ Name = 'LeBot'; Execute = 'LeBot.Host.exe' })
    }
    return @(Get-Content $ConfigPath -Raw | ConvertFrom-Json)
}

# A copy started by hand is invisible to the task: the task would start a *second* one on
# its next tick, and two long-pollers on one token is a Telegram 409. So adopt by
# replacement — stop what is running, let the task own it from now on.
function Stop-RunningCopy {
    param([string] $Match, [string] $ExeName)

    # Narrow to plausible hosts before matching command lines, so a loose Match string
    # cannot reach across the machine and stop something unrelated.
    $names = @($ExeName, 'pwsh.exe', 'powershell.exe', 'cmd.exe') | Select-Object -Unique
    $candidates = @(
        Get-CimInstance Win32_Process |
            Where-Object { $_.Name -in $names -and $_.CommandLine -and $_.CommandLine -like "*$Match*" } |
            Where-Object { $_.ProcessId -ne $PID }
    )

    foreach ($proc in $candidates) {
        $trimmed = $proc.CommandLine.Substring(0, [Math]::Min(90, $proc.CommandLine.Length))
        Write-Host "    stopping orphan pid $($proc.ProcessId): $trimmed"
        # /T: a .cmd wrapper holds the real worker as a child, and killing only the parent
        # leaves the worker behind to race the task.
        & taskkill /T /F /PID $proc.ProcessId 2>&1 | Out-Null
    }

    return $candidates.Count
}

function Set-StartupShortcut {
    param([string] $FileName, [switch] $Enable)

    $active   = Join-Path $StartupDir $FileName
    $disabled = "$active.disabled"

    if ($Enable) {
        if (Test-Path $disabled) {
            Move-Item $disabled $active -Force
            Write-Host "    restored Startup shortcut $FileName"
        }
        return
    }

    if (Test-Path $active) {
        Move-Item $active $disabled -Force
        Write-Host "    disabled Startup shortcut $FileName (the task starts it now)"
    }
}

function Register-Entry {
    param([object] $Entry)

    $name    = $Entry.Name
    $exe     = Resolve-Executable $Entry.Execute
    # Not $args — that name is a PowerShell automatic variable.
    $taskArgs = Get-Prop $Entry 'Arguments'
    $workDir = Resolve-DeployPath (Get-Prop $Entry 'WorkingDirectory' $DeployRoot)
    $match   = Get-Prop $Entry 'Match' $Entry.Execute

    if (-not (Test-Path $exe)) { throw "$name : cannot find $($Entry.Execute)" }

    Write-Host "  $name"

    # Before the task exists, not after: registration starts the repeat ticking, and a tick
    # that lands while the hand-started copy is still alive gives you two of them.
    $stopped = Stop-RunningCopy -Match $match -ExeName ([System.IO.Path]::GetFileName($exe))

    $action = if ($taskArgs) {
        New-ScheduledTaskAction -Execute $exe -Argument $taskArgs -WorkingDirectory $workDir
    }
    else {
        New-ScheduledTaskAction -Execute $exe -WorkingDirectory $workDir
    }

    # Two triggers with different jobs: the logon one brings everything back after a
    # reboot, the repeating one is the supervisor. Its start time is midnight today —
    # already in the past — so with StartWhenAvailable it begins ticking immediately.
    $triggers = @(
        New-ScheduledTaskTrigger -AtLogOn -User $UserId
        New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes $CheckMinutes)
    )

    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    # Interactive as the desktop user, not LocalSystem: yt-dlp reads the logged-in Firefox
    # profile for cookies, and that only resolves as the real user (this is exactly what
    # broke the LocalSystem install on the old server). Limited run level keeps
    # registration free of elevation.
    $principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask -TaskName $name `
        -Action $action -Trigger $triggers -Settings $settings -Principal $principal `
        -Description 'LeBot deployment process - autostart and self-heal' `
        -Force | Out-Null

    $shortcut = Get-Prop $Entry 'StartupShortcut'
    if ($shortcut -and -not $KeepStartupShortcuts) { Set-StartupShortcut -FileName $shortcut }

    Start-ScheduledTask -TaskName $name
    Write-Host "    registered, every ${CheckMinutes}m, started$(if ($stopped) { " (replaced $stopped running copy/copies)" })"
}

function Unregister-Entry {
    param([object] $Entry)

    $name = $Entry.Name
    Write-Host "  $name"

    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
        Write-Host '    task removed (the process itself keeps running until you stop it)'
    }
    else {
        Write-Host '    no such task'
    }

    $shortcut = Get-Prop $Entry 'StartupShortcut'
    if ($shortcut) { Set-StartupShortcut -FileName $shortcut -Enable }
}

# ------------------------------------------------------------------------------- main

if (-not (Test-Path $DeployRoot)) { throw "no deployment at $DeployRoot" }

$entries = Get-Entries
if ($entries.Count -eq 0) { throw "$ConfigPath lists no processes" }

if ($Uninstall) {
    Write-Host "Removing autostart tasks ($($entries.Count)):"
    foreach ($entry in $entries) { Unregister-Entry $entry }
    Write-Host ''
    Write-Host 'Done. Nothing supervises these processes now.'
    return
}

Write-Host "Registering autostart tasks as $UserId ($($entries.Count)):"
foreach ($entry in $entries) { Register-Entry $entry }

Write-Host ''
Get-ScheduledTask -TaskName $entries.Name |
    Get-ScheduledTaskInfo |
    Select-Object TaskName, LastRunTime, LastTaskResult, NextRunTime |
    Format-Table -AutoSize

Write-Host 'Done. A dead process is restarted within the check interval; all of them come back at logon.'
