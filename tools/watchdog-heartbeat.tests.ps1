#Requires -Version 7
<#
.SYNOPSIS
    Scenario checks for the watchdog's heartbeat logic (Watch-BotHeartbeat, Restart-StuckBot).

.DESCRIPTION
    Lifts those functions out of watchdog.ps1 and runs them against stubs: no real process,
    scheduled task or Telegram message is touched. Exits non-zero if any scenario fails.

.EXAMPLE
    pwsh -NoProfile -File tools/watchdog-heartbeat.tests.ps1
#>
param([string] $ScriptPath = (Join-Path $PSScriptRoot 'watchdog.ps1'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$null)
$wanted = 'Get-Prop', 'Read-Heartbeat', 'Read-Instant', 'Watch-BotHeartbeat', 'Restart-StuckBot'
foreach ($f in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($wanted -contains $f.Name) { . ([scriptblock]::Create($f.Extent.Text)) }
}

# Parameters the script would have.
$BotProcessName = 'LeBot.Host'; $BotTaskName = 'LeBot'
$HeartbeatStaleMinutes = 5; $TelegramUnreachableMinutes = 10; $BotRestartCooldownMinutes = 30
$HeartbeatPath = Join-Path ([IO.Path]::GetTempPath()) "wd-harness-$PID.json"

# Stubs: nothing real is touched.
$global:Calls = [System.Collections.Generic.List[string]]::new()
$global:FakeBots = @()
$global:TaskState = 'Ready'
function Write-Log { param($Message, $Level = 'INF') $global:Calls.Add("LOG[$Level] $Message") }
function Send-OperatorAlert { param($Text) $global:Calls.Add("ALERT $Text") }
function Get-Process { param($Name, $ErrorAction) $global:FakeBots }
function Get-ScheduledTask { param($TaskName, $ErrorAction) [pscustomobject]@{ State = $global:TaskState } }
function Stop-ScheduledTask { param($TaskName) $global:Calls.Add("STOP $TaskName") }
function Start-ScheduledTask { param($TaskName) $global:Calls.Add("START $TaskName") }
function Get-CimInstance { param($ClassName) @([pscustomobject]@{ ProcessId = 777; ParentProcessId = 4242 }, [pscustomobject]@{ ProcessId = 1; ParentProcessId = 9 }) }
function taskkill.exe { $global:Calls.Add("TASKKILL $($args -join ' ')") }
function Stop-Process { param([switch] $Force) process { $global:Calls.Add('KILL bot') } }
function Start-Sleep { param($Seconds) }

function Reset {
    $script:HeartbeatLastCheck = $null; $script:HeartbeatGraceUntil = $null; $script:HeartbeatMissingLogged = $false
    $script:BotRestartedAt = $null; $script:BotStuckAlerted = $false; $script:TelegramDownAlerted = $false
    $global:FakeBots = @([pscustomobject]@{ Id = 4242; StartTime = (Get-Date).AddMinutes(-60) })
    $global:TaskState = 'Ready'
}
function Beat { param($LoopAgo, $WrittenAgo = 0.3, $OkAgo = $LoopAgo, $BeatPid = 4242)
    $now = [datetimeoffset]::UtcNow
    $j = [ordered]@{ pid = $BeatPid; version = '1.2.3'; writtenAt = $now.AddMinutes(-$WrittenAgo).ToString('o')
        loopAt = if ($null -eq $LoopAgo) { $null } else { $now.AddMinutes(-$LoopAgo).ToString('o') }
        polledOkAt = if ($null -eq $OkAgo) { $null } else { $now.AddMinutes(-$OkAgo).ToString('o') } }
    $j | ConvertTo-Json | Set-Content $HeartbeatPath
}
$failed = 0
function Tick { $global:Calls.Clear(); Watch-BotHeartbeat; @($global:Calls) }
function Check { param([string] $Name, [bool] $Ok, $Seen)
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name`n      saw: $($Seen -join ' || ')"; $script:failed++ }
}

Reset; Beat -LoopAgo 0.5
$c = @(Tick); Check 'fresh heartbeat -> nothing' ($c.Count -eq 0) $c

Reset; Beat -LoopAgo 6
$c = @(Tick)
Check 'loop stale 6m -> stop, kill child tree, start, alert' (($c -match '^STOP LeBot').Count -eq 1 -and ($c -match '^TASKKILL /PID 777').Count -eq 1 -and ($c -match '^START LeBot').Count -eq 1 -and ($c -match 'ALERT .*не опитував Telegram 6 хв.*перезапустив').Count -eq 1) $c
$c = @(Tick); Check 'still stuck within cooldown -> one alert, no restart' (($c -match '^STOP').Count -eq 0 -and ($c -match 'ALERT .*одразу після перезапуску').Count -eq 1) $c
$c = @(Tick); Check 'still stuck again -> silent' (($c -match '^(STOP|ALERT)').Count -eq 0) $c
Beat -LoopAgo 0.2
$c = @(Tick); Check 'bot polls again -> logged, no alert' (($c -match 'bot polls again').Count -eq 1 -and ($c -match '^ALERT').Count -eq 0) $c

Reset; Beat -LoopAgo 0.5 -WrittenAgo 7
$c = @(Tick); Check 'whole process frozen -> restart' (($c -match '^STOP').Count -eq 1 -and ($c -match 'не оновлював пульс 7 хв').Count -ge 1) $c

Reset; Beat -LoopAgo 6; $global:TaskState = 'Disabled'
$c = @(Tick); Check 'task disabled (deploy) -> leave it' (($c -match '^STOP').Count -eq 0 -and ($c -match 'disabled').Count -eq 1) $c

Reset; Beat -LoopAgo 6 -BeatPid 1111
$c = @(Tick); Check 'heartbeat of another pid -> not judged, logged once' (($c -match '^STOP').Count -eq 0 -and ($c -match 'writes no heartbeat').Count -eq 1) $c
$c = @(Tick); Check '... and not logged twice' ($c.Count -eq 0) $c

Reset; Remove-Item $HeartbeatPath
$c = @(Tick); Check 'no heartbeat file (old build) -> not judged' (($c -match '^STOP').Count -eq 0) $c

Reset; $global:FakeBots = @([pscustomobject]@{ Id = 4242; StartTime = (Get-Date).AddMinutes(-2) }); Beat -LoopAgo $null -OkAgo $null
$c = @(Tick); Check 'bot started 2m ago -> grace' ($c.Count -eq 0) $c

Reset; Beat -LoopAgo $null -OkAgo $null
$c = @(Tick); Check 'up 60m, loop never turned -> restart' (($c -match '^STOP').Count -eq 1) $c

Reset; Beat -LoopAgo 0.3 -OkAgo 12
$c = @(Tick); Check 'loop turns, Telegram silent 12m -> alert, no restart' (($c -match '^STOP').Count -eq 0 -and ($c -match 'ALERT .*не може достукатися до Telegram').Count -eq 1) $c
$c = @(Tick); Check '... alerted once' ($c.Count -eq 0) $c
Beat -LoopAgo 0.3 -OkAgo 0.3
$c = @(Tick); Check 'Telegram back -> recovery alert' (($c -match 'ALERT .*знову відповідає').Count -eq 1) $c

Reset; Beat -LoopAgo 6; $script:HeartbeatLastCheck = [datetime]::UtcNow.AddMinutes(-10)
$c = @(Tick); Check 'watchdog tick gap (machine slept) -> grace, no restart' (($c -match '^STOP').Count -eq 0) $c

Reset; $global:FakeBots = @([pscustomobject]@{ Id = 1; StartTime = (Get-Date).AddMinutes(-60) }, [pscustomobject]@{ Id = 2; StartTime = (Get-Date).AddMinutes(-60) }); Beat -LoopAgo 6
$c = @(Tick); Check 'two bot processes -> not judged' ($c.Count -eq 0) $c

Remove-Item $HeartbeatPath -ErrorAction SilentlyContinue
"$failed failed"
if ($failed) { exit 1 }
