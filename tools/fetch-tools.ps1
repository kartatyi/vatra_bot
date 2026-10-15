#!/usr/bin/env pwsh
# Downloads yt-dlp.exe and deno.exe into tools/yt-dlp/. Idempotent: skips what is already present.
# Run from anywhere; resolves paths relative to the script location.
#
# deno is the JS runtime yt-dlp solves YouTube's player challenge with — without it every
# YouTube download comes back 403 Forbidden.

$ErrorActionPreference = 'Stop'
$progressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $repoRoot 'tools/yt-dlp'
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

$ytDlpPath = Join-Path $toolsDir 'yt-dlp.exe'
if (Test-Path $ytDlpPath) {
    Write-Host "yt-dlp already present at $ytDlpPath"
} else {
    $url = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe'
    Write-Host "Downloading yt-dlp from $url ..."
    Invoke-WebRequest -Uri $url -OutFile $ytDlpPath -UseBasicParsing
    Write-Host "yt-dlp downloaded to $ytDlpPath"
}

$denoPath = Join-Path $toolsDir 'deno.exe'
if (Test-Path $denoPath) {
    Write-Host "deno already present at $denoPath"
} else {
    $url = 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip'
    $zip = Join-Path ([System.IO.Path]::GetTempPath()) "deno-$PID.zip"
    Write-Host "Downloading deno from $url ..."
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $toolsDir -Force
    Remove-Item $zip
    Write-Host "deno downloaded to $denoPath"
}

Write-Host ""
Write-Host "Next: set your Telegram bot token via user-secrets:"
Write-Host '    dotnet user-secrets set "Telegram:BotToken" "<your-token>" --project src/LeBot.Host'
