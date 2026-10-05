#Requires -Version 7
<#
.SYNOPSIS
    Watches the repost journal for new extraction failures and hands each batch to a
    headless Claude Code run that diagnoses, fixes, and reports.

.DESCRIPTION
    The loop itself is deliberately dumb and cheap: it stats the SQLite WAL file and only
    queries the journal when the database actually changed, so an idle bot costs nothing —
    no agent is started until a real failure lands.

    The same tick also checks that the bot process exists at all, and alerts once if it
    has been gone longer than the grace period. It never restarts it — that is the
    scheduled task's job (tools/install-autostart.ps1).

    Every few minutes it also probes the dashboard's API, and restarts the dashboard through
    its scheduled task when a live reader keeps answering with 5xx or not at all — a wedge the
    task cannot see, because the process never dies.

    Everything identifying the deployment (report chat id, bot token) is read at runtime
    from files under -DeployRoot. Nothing in this repository may carry it.

    Kill switch: create a file named PAUSE in <DeployRoot>\watchdog\ and the loop goes
    quiet without stopping. Delete it to resume.

.EXAMPLE
    pwsh -NoProfile -File tools/watchdog.ps1
.EXAMPLE
    pwsh -NoProfile -File tools/watchdog.ps1 -Once -ObserveOnly
#>
[CmdletBinding()]
param(
    # Where the running bot lives — journal, logs, config, watchdog state.
    [string] $DeployRoot = 'D:\lebot',

    # Working copy the agent is allowed to patch.
    [string] $RepoRoot = 'E:\vatra_bot',

    # Journal poll interval. Cheap: a file stat, then an indexed query only if it moved.
    [int] $PollSeconds = 10,

    # Run a single pass and exit — for testing the wiring.
    [switch] $Once,

    # Forbid the agent from deploying: it may diagnose, fix tooling, deliver media and
    # commit, but not swap the exe or restart the bot.
    [switch] $ObserveOnly,

    # Hard ceiling on one agent run before it is killed.
    [int] $AgentTimeoutMinutes = 30,

    # Process the bot runs as, watched for liveness alongside the journal.
    [string] $BotProcessName = 'LeBot.Host',

    # How long the bot may be missing before it counts as down rather than restarting.
    # Task Scheduler retries every minute, so this is a few of its ticks.
    [int] $LivenessGraceMinutes = 3,

    # A dashboard endpoint that has to read the journal to answer, probed for the wedge a
    # live reader can fall into. Empty skips the probe (a deployment without a dashboard).
    [string] $DashboardProbeUrl = 'http://127.0.0.1:5005/api/data',

    # The scheduled task that owns the dashboard, and the process it runs as.
    [string] $DashboardTaskName = 'LeBot Dashboard',
    [string] $DashboardProcessName = 'LeBot.Dashboard',

    # Minutes between probes, and how many failed probes in a row mean wedged rather than slow.
    [int] $DashboardProbeMinutes = 2,
    [int] $DashboardFailuresBeforeRestart = 2,

    # A dashboard that wedges again this soon after a restart is not cured by restarting: the
    # operator hears about it instead of getting a restart loop.
    [int] $DashboardRestartCooldownMinutes = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$StateDir    = Join-Path $DeployRoot 'watchdog'
$RunsDir     = Join-Path $StateDir 'runs'
$BackupDir   = Join-Path $StateDir 'backup'
$ConfigPath  = Join-Path $StateDir 'config.local.json'
$StatePath   = Join-Path $StateDir 'state.json'
$PausePath   = Join-Path $StateDir 'PAUSE'
$LogPath     = Join-Path $StateDir 'watchdog.log'
$DbPath      = Join-Path $DeployRoot 'data\lebot.db'
$LocalConfig = Join-Path $DeployRoot 'appsettings.Local.json'
$PromptPath  = Join-Path $PSScriptRoot 'watchdog-prompt.md'

foreach ($dir in @($StateDir, $RunsDir, $BackupDir)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

# Strict mode turns a missing property into a terminating error, which would take out the
# very error paths that have to survive a half-written config.
function Get-Prop {
    param([object] $Object, [string] $Name, [object] $Default = $null)

    if ($null -eq $Object) { return $Default }
    # Piping rather than .Properties.Name: member enumeration over an *empty* property
    # collection is itself a strict-mode error.
    if (-not ($Object.PSObject.Properties | Where-Object { $_.Name -eq $Name })) { return $Default }
    return $Object.$Name
}

function Write-Log {
    param([string] $Message, [string] $Level = 'INF')

    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    Add-Content -Path $LogPath -Value $line -Encoding utf8
}

function Get-BotToken {
    if (-not (Test-Path $LocalConfig)) { return $null }
    try {
        $json = Get-Content $LocalConfig -Raw | ConvertFrom-Json
        return $json.Telegram.BotToken
    }
    catch {
        Write-Log "could not read the bot token: $($_.Exception.Message)" 'WRN'
        return $null
    }
}

# Best-effort operator alert. The agent sends its own detailed report; this is only for
# the cases where the agent never got to run or died.
function Send-OperatorAlert {
    param([string] $Text)

    $token = Get-BotToken
    $chatId = Get-Prop $script:Config 'ReportChatId'
    if (-not $token -or -not $chatId) { return }

    try {
        $null = Invoke-RestMethod -Method Post -TimeoutSec 20 `
            -Uri "https://api.telegram.org/bot$token/sendMessage" `
            -Body @{ chat_id = $chatId; text = $Text }
    }
    catch {
        Write-Log "telegram alert failed: $($_.Exception.Message)" 'WRN'
    }
}

# Liveness is a different question from the journal, and the journal cannot answer it: a
# dead bot writes no failures, so to the loop below a dead bot looks exactly like a quiet
# night. That is how 2026-08-13 went unnoticed for fourteen hours.
#
# This only reports. Restarting belongs to the scheduled task (tools/install-autostart.ps1)
# — two supervisors racing to start one bot means two long-pollers on one token, which
# Telegram answers with a 409 and neither instance survives cleanly.
function Watch-BotLiveness {
    if (@(Get-Process -Name $BotProcessName -ErrorAction SilentlyContinue).Count -gt 0) {
        if ($script:BotDownSince) {
            $downMinutes = [int] ([datetime]::UtcNow - $script:BotDownSince).TotalMinutes
            Write-Log "bot process is back after ${downMinutes}m"
            if ($script:BotDownAlerted) {
                Send-OperatorAlert "Watchdog: бот знову працює, простій ~${downMinutes} хв."
            }
        }
        $script:BotDownSince   = $null
        $script:BotDownAlerted = $false
        return
    }

    if (-not $script:BotDownSince) {
        $script:BotDownSince = [datetime]::UtcNow
        Write-Log "bot process '$BotProcessName' is gone — giving the scheduled task ${LivenessGraceMinutes}m" 'WRN'
        return
    }

    if (-not $script:BotDownAlerted -and
        ([datetime]::UtcNow - $script:BotDownSince).TotalMinutes -ge $LivenessGraceMinutes) {
        $script:BotDownAlerted = $true
        Write-Log "bot process '$BotProcessName' still down after ${LivenessGraceMinutes}m" 'ERR'
        Send-OperatorAlert "Watchdog: бот лежить уже ${LivenessGraceMinutes}+ хв і не піднявся сам. Перевір задачу планувальника «LeBot»."
    }
}

# A dashboard can be alive and still useless. Twice a reader that had been up for days began
# answering every /api/* call with a 500 while its page kept loading, and a fresh process on
# the same database served fine. The task cannot see that — it only restarts a process that
# died — so this probe does, and restarts the reader through its task, never around it.
#
# Unlike the bot, the dashboard is safe to restart from here: it holds no Telegram token, so a
# second copy is a port clash for a few seconds, not a 409.
function Watch-DashboardHealth {
    if (-not $DashboardProbeUrl) { return }
    if ([datetime]::UtcNow -lt $script:DashboardNextProbe) { return }
    $script:DashboardNextProbe = [datetime]::UtcNow.AddMinutes($DashboardProbeMinutes)

    # A reader that is not running at all is the task's to bring back.
    if (@(Get-Process -Name $DashboardProcessName -ErrorAction SilentlyContinue).Count -eq 0) {
        $script:DashboardFailures = 0
        return
    }

    $problem = $null
    try {
        $null = Invoke-WebRequest -Uri $DashboardProbeUrl -TimeoutSec 30 -UseBasicParsing
    }
    catch {
        # Get-Prop, not .Response: a timeout carries no response, and strict mode would throw.
        $response = Get-Prop $_.Exception 'Response'
        $status = if ($response) { [int] $response.StatusCode } else { 0 }
        # A 4xx means the probe asks the wrong question, which a restart would not change.
        if ($status -eq 0 -or $status -ge 500) {
            $problem = if ($status) { "HTTP $status" } else { $_.Exception.Message }
        }
    }

    if (-not $problem) {
        if ($script:DashboardFailures -gt 0 -or $script:DashboardHealAlerted) {
            Write-Log 'dashboard answers again'
        }
        $script:DashboardFailures = 0
        $script:DashboardHealAlerted = $false
        return
    }

    $script:DashboardFailures++
    Write-Log "dashboard probe failed ($($script:DashboardFailures)/$DashboardFailuresBeforeRestart): $problem" 'WRN'
    if ($script:DashboardFailures -lt $DashboardFailuresBeforeRestart) { return }

    if ($script:DashboardRestartedAt -and
        ([datetime]::UtcNow - $script:DashboardRestartedAt).TotalMinutes -lt $DashboardRestartCooldownMinutes) {
        if (-not $script:DashboardHealAlerted) {
            $script:DashboardHealAlerted = $true
            Write-Log 'dashboard failing again right after a restart — leaving it to the operator' 'ERR'
            Send-OperatorAlert "Watchdog: дашборд знову відповідає помилкою ($problem) одразу після перезапуску. Стек — у logs\dashboard-*.log."
        }
        return
    }

    Restart-Dashboard -Reason $problem
}

# Stop and start through the task, without the disable/enable that an exe swap needs: that
# dance stops the task restoring the old binary mid-copy, a restart has nothing to race, and a
# task left disabled by a failed step would be worse than the wedge it was meant to clear.
function Restart-Dashboard {
    param([string] $Reason)

    Write-Log "dashboard wedged ($Reason) — restarting it through task '$DashboardTaskName'" 'WRN'
    $script:DashboardRestartedAt = [datetime]::UtcNow
    $script:DashboardFailures = 0

    try {
        Stop-ScheduledTask -TaskName $DashboardTaskName
        Get-Process -Name $DashboardProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
        for ($i = 0; $i -lt 15 -and @(Get-Process -Name $DashboardProcessName -ErrorAction SilentlyContinue).Count -gt 0; $i++) {
            Start-Sleep -Seconds 1
        }
        Start-ScheduledTask -TaskName $DashboardTaskName
        Write-Log 'dashboard restarted'
    }
    catch {
        # The task's own repeating tick brings a stopped reader back within its interval anyway.
        Write-Log "dashboard restart failed: $($_.Exception.Message)" 'ERR'
    }
}

function Read-JsonFile {
    param([string] $Path, [object] $Default)

    if (-not (Test-Path $Path)) { return $Default }
    try { return Get-Content $Path -Raw | ConvertFrom-Json }
    catch {
        Write-Log "corrupt json at $Path, falling back to defaults: $($_.Exception.Message)" 'WRN'
        return $Default
    }
}

function Save-State {
    param([object] $State)
    $State | ConvertTo-Json -Depth 6 | Set-Content -Path $StatePath -Encoding utf8
}

# Collapses the volatile parts of an error — post ids, urls, timestamps — so the same
# breakage seen on three different links counts as one signature.
function Get-FailureSignature {
    param([object] $Failure)

    $reason = [string] $Failure.errorReason
    $reason = $reason -replace 'https?://\S+', '<url>'
    $reason = $reason -replace '\d{6,}', '<id>'
    $reason = $reason -replace '\s+', ' '
    if ($reason.Length -gt 120) { $reason = $reason.Substring(0, 120) }

    return '{0}|{1}|{2}' -f $Failure.host, $Failure.errorVariant, $reason.Trim()
}

# Signatures are only used for the 24h run cap, so anything older is dead weight in the
# state file.
function Remove-StaleSignatures {
    param([object] $State)

    $cutoff = [datetime]::UtcNow.AddDays(-7)
    foreach ($name in @($State.signatures.PSObject.Properties | ForEach-Object { $_.Name })) {
        $entry = $State.signatures.$name
        $last = [datetime]::Parse(
            (Get-Prop $entry 'lastUtc' '2000-01-01T00:00:00.0000000Z'),
            [cultureinfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind)
        if ($last -lt $cutoff) { $State.signatures.PSObject.Properties.Remove($name) }
    }
}

function Get-DbStamp {
    $stamp = ''
    foreach ($file in @($DbPath, "$DbPath-wal")) {
        if (Test-Path $file) {
            $item = Get-Item $file -Force
            $stamp += '{0}:{1};' -f $item.LastWriteTimeUtc.Ticks, $item.Length
        }
    }
    return $stamp
}

function Get-NewFailures {
    param([long] $AfterId)

    $query = @"
SELECT Id AS id, OccurredAt AS occurredAt, Host AS host, Url AS url,
       ChatId AS chatId, TelegramMessageId AS telegramMessageId,
       ErrorVariant AS errorVariant, ErrorReason AS errorReason, Extractor AS extractor
FROM RepostEvents
WHERE Outcome = 'Failure' AND Id > $AfterId
ORDER BY Id;
"@

    $raw = & sqlite3 -readonly -json $DbPath $query 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlite3 failed: $raw" }
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }

    return @($raw | ConvertFrom-Json)
}

function Get-MaxFailureId {
    $raw = & sqlite3 -readonly $DbPath "SELECT COALESCE(MAX(Id), 0) FROM RepostEvents;" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlite3 failed: $raw" }
    return [long] ($raw | Select-Object -First 1)
}

function Invoke-Agent {
    param([string] $InputPath, [string] $RunId)

    $promptFile = Join-Path $RunsDir "$RunId-prompt.txt"
    $outFile    = Join-Path $RunsDir "$RunId.md"
    $errFile    = Join-Path $RunsDir "$RunId.err.txt"

    $context = @"

---

# RUNTIME CONTEXT (generated per run — trust this over anything above)

- Failure batch (JSON): $InputPath
- Repository (patchable):  $RepoRoot
- Deployment (live bot):   $DeployRoot
- Watchdog state dir:      $StateDir
- Exe backups go to:       $BackupDir
- Local config (chat id + knobs): $ConfigPath
- Bot token lives in: $LocalConfig  (key Telegram:BotToken — read it, never print or copy it)
- Run id: $RunId
- Deploy allowed this run: $(if ($ObserveOnly) { 'NO — observe-only mode, diagnose/fix tooling/deliver/commit but do not swap the exe or restart the bot' } else { 'YES — subject to the deploy budget in the rules above' })
"@

    Set-Content -Path $promptFile -Value ((Get-Content $PromptPath -Raw) + $context) -Encoding utf8

    # Not $args — that name is a PowerShell automatic variable.
    $claudeArgs = @(
        '-p'
        '--output-format', 'text'
        '--permission-mode', 'bypassPermissions'
        '--add-dir', $DeployRoot
    )

    $proc = Start-Process -FilePath 'claude' -ArgumentList $claudeArgs `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardInput $promptFile `
        -RedirectStandardOutput $outFile `
        -RedirectStandardError $errFile `
        -NoNewWindow -PassThru

    if (-not $proc.WaitForExit($AgentTimeoutMinutes * 60 * 1000)) {
        Write-Log "agent run $RunId exceeded $AgentTimeoutMinutes min — killing" 'WRN'
        & taskkill /T /F /PID $proc.Id 2>&1 | Out-Null
        return [pscustomobject]@{ ExitCode = -1; Output = ''; TimedOut = $true; OutFile = $outFile }
    }

    $output = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { '' }
    return [pscustomobject]@{
        ExitCode = $proc.ExitCode
        Output   = $output
        TimedOut = $false
        OutFile  = $outFile
    }
}

# ---------------------------------------------------------------------------- bootstrap

$script:Config = Read-JsonFile -Path $ConfigPath -Default $null
if (-not $script:Config) {
    throw "missing $ConfigPath — see tools/watchdog-prompt.md for the expected keys"
}

if (-not (Test-Path $PromptPath)) { throw "missing agent prompt at $PromptPath" }
if (-not (Test-Path $DbPath)) { throw "no journal database at $DbPath" }

$state = Read-JsonFile -Path $StatePath -Default $null
if (-not $state) {
    # First start: begin at the present, never replay history.
    $state = [pscustomobject]@{
        checkpointId = Get-MaxFailureId
        signatures   = [pscustomobject]@{}
    }
    Save-State $state
    Write-Log "seeded state at event id $($state.checkpointId)"
}

$maxRuns = [int] (Get-Prop $script:Config 'MaxAgentRunsPerSignaturePerDay' 2)

Write-Log "watchdog up — poll ${PollSeconds}s, checkpoint $($state.checkpointId), observe-only=$($ObserveOnly.IsPresent)"

$lastStamp = ''
$authAlerted = $false
$script:BotDownSince = $null
$script:BotDownAlerted = $false
$script:DashboardNextProbe = [datetime]::MinValue
$script:DashboardFailures = 0
$script:DashboardRestartedAt = $null
$script:DashboardHealAlerted = $false

while ($true) {
    try {
        # Ahead of the PAUSE check deliberately: PAUSE silences agent runs, but "the bot is
        # not running" is the one thing you always want to hear — the more so because the
        # 401 auto-pause path below writes PAUSE itself. The dashboard probe sits here for
        # the same reason: it involves no agent, and an auto-pause must not switch it off.
        Watch-BotLiveness
        Watch-DashboardHealth

        if (Test-Path $PausePath) {
            if (-not $Once) { Start-Sleep -Seconds $PollSeconds; continue }
            Write-Log 'PAUSE file present — nothing to do'
            break
        }

        $stamp = Get-DbStamp
        if ($stamp -ne $lastStamp) {
            $lastStamp = $stamp

            # @() at the call site: PowerShell unrolls an empty array on return, so
            # without this an empty result arrives as $null and .Count throws.
            $failures = @(Get-NewFailures -AfterId $state.checkpointId)
            if ($failures.Count -gt 0) {
                # Advance the checkpoint before doing any work: a crashing agent must not
                # make the same batch fire forever.
                $state.checkpointId = [long] ($failures[-1].id)
                Save-State $state

                Remove-StaleSignatures $state

                $fresh = @()
                foreach ($failure in $failures) {
                    $sig = Get-FailureSignature $failure
                    $seen = Get-Prop $state.signatures $sig

                    $withinDay = $false
                    if ($seen) {
                        $lastUtc = [datetime]::Parse(
                            (Get-Prop $seen 'lastUtc' '2000-01-01T00:00:00.0000000Z'),
                            [cultureinfo]::InvariantCulture,
                            [System.Globalization.DateTimeStyles]::RoundtripKind)
                        $withinDay = ([datetime]::UtcNow - $lastUtc).TotalHours -lt 24
                    }

                    $seenCount = [int] (Get-Prop $seen 'count' 0)
                    if ($withinDay -and $seenCount -ge $maxRuns) {
                        Write-Log "skipping event $($failure.id): signature already investigated ${seenCount}x today" 'WRN'
                        continue
                    }

                    $count = if ($withinDay) { $seenCount + 1 } else { 1 }
                    $state.signatures | Add-Member -NotePropertyName $sig -NotePropertyValue ([pscustomobject]@{
                        count   = $count
                        lastUtc = [datetime]::UtcNow.ToString('o')
                    }) -Force

                    $fresh += $failure
                }

                Save-State $state

                if ($fresh.Count -gt 0) {
                    $runId = Get-Date -Format 'yyyyMMdd-HHmmss'
                    $inputPath = Join-Path $RunsDir "$runId-input.json"
                    $fresh | ConvertTo-Json -Depth 6 -AsArray | Set-Content -Path $inputPath -Encoding utf8

                    Write-Log "$($fresh.Count) new failure(s) — starting agent run $runId"
                    $result = Invoke-Agent -InputPath $inputPath -RunId $runId

                    if ($result.TimedOut) {
                        Send-OperatorAlert "Watchdog: агент завис на прогоні $runId і був знятий по таймауту ($AgentTimeoutMinutes хв). Лог: $($result.OutFile)"
                    }
                    elseif ($result.ExitCode -ne 0) {
                        $isAuth = $result.Output -match 'authenticate|OAuth|401'
                        Write-Log "agent run $runId exited with $($result.ExitCode)" 'WRN'

                        if ($isAuth) {
                            if (-not $authAlerted) {
                                Send-OperatorAlert 'Watchdog зупинено: Claude CLI не авторизований (401). Запусти `claude` у терміналі, зроби /login, потім видали файл watchdog\PAUSE.'
                                $authAlerted = $true
                            }
                            Set-Content -Path $PausePath -Value 'auto-paused: claude CLI is not authenticated' -Encoding utf8
                            Write-Log 'auto-paused — claude CLI is not authenticated' 'ERR'
                        }
                        else {
                            Send-OperatorAlert "Watchdog: прогін $runId впав з кодом $($result.ExitCode). Лог: $($result.OutFile)"
                        }
                    }
                    else {
                        Write-Log "agent run $runId finished"
                    }
                }
            }
        }
    }
    catch {
        Write-Log "tick failed: $($_.Exception.Message)" 'ERR'
    }

    if ($Once) { break }
    Start-Sleep -Seconds $PollSeconds
}
