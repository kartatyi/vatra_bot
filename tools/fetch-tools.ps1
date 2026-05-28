#!/usr/bin/env pwsh
# Downloads yt-dlp.exe into tools/yt-dlp/. Idempotent: skips if already present.
# Run from anywhere; resolves paths relative to the script location.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $repoRoot 'tools/yt-dlp'
$ytDlpPath = Join-Path $toolsDir 'yt-dlp.exe'

if (Test-Path $ytDlpPath) {
    Write-Host "yt-dlp already present at $ytDlpPath"
    exit 0
}

New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

$url = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe'
Write-Host "Downloading yt-dlp from $url ..."

$progressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $url -OutFile $ytDlpPath -UseBasicParsing

Write-Host "yt-dlp downloaded to $ytDlpPath"
Write-Host ""
Write-Host "Next: set your Telegram bot token via user-secrets:"
Write-Host '    dotnet user-secrets set "Telegram:BotToken" "<your-token>" --project src/LeBot.Host'
