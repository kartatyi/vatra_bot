#!/usr/bin/env pwsh
# Produces self-contained single-file Windows x64 builds of the bot AND the dashboard reader.
# Output: publish/LeBot.Host.exe + publish/LeBot.Dashboard.exe (~80 MB each; .NET 10 baked in).
#
# Usage:
#   pwsh tools/publish.ps1                       # release build at publish/
#   pwsh tools/publish.ps1 -OutputPath C:\bot    # custom destination
#   pwsh tools/publish.ps1 -Runtime linux-x64    # cross-compile for Linux
#   pwsh tools/publish.ps1 -SkipDashboard        # bot only (the old behaviour)

param(
    [string]$OutputPath = "publish",
    [string]$Runtime    = "win-x64",
    [string]$Configuration = "Release",
    # Stamped into the assembly so the running bot knows its own version. The release
    # workflow passes the git tag (e.g. 1.4.0); a manual run keeps the 0.0.0 dev default.
    [string]$Version    = "0.0.0",
    [switch]$SkipDashboard
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$absoluteOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }

function Publish-Project([string]$projectPath, [string]$label) {
    Write-Host "Publishing $label ($Runtime, $Configuration) ..."
    dotnet publish $projectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=embedded `
        -p:Version=$Version `
        -p:InformationalVersion=$Version `
        -o $absoluteOutput
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish of $label failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

if (Test-Path $absoluteOutput) {
    Write-Host "Cleaning $absoluteOutput ..."
    Remove-Item -Recurse -Force $absoluteOutput
}

# The bot first, then the dashboard reader into the same folder so they sit side by side and share the
# beside-the-exe data/lebot.db (the dashboard reads what the bot writes).
Publish-Project (Join-Path $repoRoot "src/LeBot.Host/LeBot.Host.csproj") "LeBot.Host"
if (-not $SkipDashboard) {
    Publish-Project (Join-Path $repoRoot "src/LeBot.Dashboard/LeBot.Dashboard.csproj") "LeBot.Dashboard"
}

Write-Host ""
Write-Host "Done. Self-contained build in: $absoluteOutput"
Get-ChildItem $absoluteOutput -Filter "LeBot.*.exe" | Format-Table Name, Length

Write-Host ""
Write-Host "Next steps on the server (Windows):"
Write-Host "  1. Copy both .exe files to the destination folder (e.g. C:\LeBot\)."
Write-Host "  2. Open an admin command prompt there."
Write-Host "  3. Run:  .\LeBot.Host.exe --install"
Write-Host "     The installer will prompt for the bot token, download yt-dlp,"
Write-Host "     create the runtime folders, register a Scheduled Task that runs"
Write-Host "     at boot under LocalSystem with restart-on-failure, and start it."
Write-Host "  4. (Optional) Run the dashboard reader beside it:  .\LeBot.Dashboard.exe"
Write-Host "     then reach http://localhost:5005 over your SSH tunnel."
Write-Host ""
Write-Host "To remove later:  .\LeBot.Host.exe --uninstall"
