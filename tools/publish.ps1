#!/usr/bin/env pwsh
# Produces a self-contained single-file Windows x64 build of LeBot.Host.
# Output: publish/LeBot.Host.exe (~80 MB; .NET 10 runtime baked in).
#
# Usage:
#   pwsh tools/publish.ps1                       # release build at publish/
#   pwsh tools/publish.ps1 -OutputPath C:\bot    # custom destination
#   pwsh tools/publish.ps1 -Runtime linux-x64    # cross-compile for Linux

param(
    [string]$OutputPath = "publish",
    [string]$Runtime    = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot "src/LeBot.Host/LeBot.Host.csproj"
$absoluteOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }

if (Test-Path $absoluteOutput) {
    Write-Host "Cleaning $absoluteOutput ..."
    Remove-Item -Recurse -Force $absoluteOutput
}

Write-Host "Publishing LeBot.Host ($Runtime, $Configuration) ..."
dotnet publish $hostProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -o $absoluteOutput

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Done. Self-contained build in: $absoluteOutput"
Get-ChildItem $absoluteOutput -Filter "LeBot.Host*" | Format-Table Name, Length

Write-Host ""
Write-Host "Next steps on the server (Windows):"
Write-Host "  1. Copy LeBot.Host.exe to the destination folder (e.g. C:\LeBot\)."
Write-Host "  2. Open an admin command prompt there."
Write-Host "  3. Run:  .\LeBot.Host.exe --install"
Write-Host "     The installer will prompt for the bot token, download yt-dlp,"
Write-Host "     create the runtime folders, register a Scheduled Task that runs"
Write-Host "     at boot under LocalSystem with restart-on-failure, and start it."
Write-Host ""
Write-Host "To remove later:  .\LeBot.Host.exe --uninstall"
