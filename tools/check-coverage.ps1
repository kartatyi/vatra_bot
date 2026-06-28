#!/usr/bin/env pwsh
# Fails the build when per-layer line coverage drops below its enforced floor.
#
# The floors are a ratchet, not the goal. CLAUDE.md documents the targets we are
# climbing toward (Domain >= 90 %, Application >= 80 %, overall >= 70 %); until the
# suite reaches them, these floors lock in current coverage so a change can only
# raise it, never quietly erode it. Raise a floor toward its target in the same PR
# that adds the tests clearing it. Never lower one.
#
# Reads ReportGenerator's JsonSummary (Summary.json), not the raw cobertura files,
# because that report has already merged the per-test-project coverage by line.
#
# Usage:
#   pwsh tools/check-coverage.ps1 -SummaryPath coverage-report/Summary.json

param(
    [Parameter(Mandatory)][string]$SummaryPath,
    [double]$DomainMin = 77,
    [double]$ApplicationMin = 90,
    [double]$OverallMin = 36
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SummaryPath)) {
    Write-Error "Coverage summary not found at '$SummaryPath'. Did ReportGenerator run?"
    exit 1
}

$report = Get-Content $SummaryPath -Raw | ConvertFrom-Json

function Get-LayerCoverage([string]$assembly) {
    $match = $report.coverage.assemblies | Where-Object { $_.name -eq $assembly }
    if ($null -eq $match) {
        Write-Error "Assembly '$assembly' is absent from the coverage report; cannot gate on it."
        exit 1
    }
    return [double]$match.coverage
}

$checks = @(
    [pscustomobject]@{ Layer = "Domain"; Actual = (Get-LayerCoverage "LeBot.Domain"); Floor = $DomainMin }
    [pscustomobject]@{ Layer = "Application"; Actual = (Get-LayerCoverage "LeBot.Application"); Floor = $ApplicationMin }
    [pscustomobject]@{ Layer = "Overall"; Actual = [double]$report.summary.linecoverage; Floor = $OverallMin }
)

$failed = $false
foreach ($check in $checks) {
    $passed = $check.Actual -ge $check.Floor
    if (-not $passed) { $failed = $true }
    $verdict = if ($passed) { "PASS" } else { "FAIL" }
    "{0,-12} {1,6:N1} %  (floor {2,5:N1} %)  {3}" -f $check.Layer, $check.Actual, $check.Floor, $verdict | Write-Host
}

if ($failed) {
    Write-Error "Coverage gate failed: a layer fell below its enforced floor."
    exit 1
}

Write-Host "Coverage gate passed."
