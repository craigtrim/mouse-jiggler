<#
.SYNOPSIS
    Runs the test suite.

.DESCRIPTION
    The default run is safe on a working machine: nothing moves the pointer and
    nothing holds a wake request, because the tests that do are skipped unless
    MOUSEJIGGLER_LIVE_INPUT says otherwise.

    -LiveInput turns those on. Only use it on a desktop you are willing to have
    the pointer move around on: the tests inject real input and take out a real
    keep-awake request. That is exactly why they are off by default rather than
    merely documented as risky.

    -Repeat runs the suite more than once. The IPC and single-instance tests
    involve real named pipes and a real mutex, so a defect there shows up as one
    failure in several runs rather than a reliable one.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $LiveInput,
    [switch] $NoBuild,
    [int] $Repeat = 1,
    [string] $Filter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot 'MouseJiggler.sln'
$Results = Join-Path $RepoRoot 'TestResults'

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

if ($Repeat -lt 1) { throw '-Repeat must be at least 1.' }

if ($LiveInput) {
    Write-Host 'Live input tests are ENABLED. The pointer will move and a wake request will be taken out.' -ForegroundColor Yellow
    $env:MOUSEJIGGLER_LIVE_INPUT = '1'
}
else {
    # Cleared rather than left alone, so a variable set earlier in this shell
    # cannot quietly turn them on.
    $env:MOUSEJIGGLER_LIVE_INPUT = $null
}

$arguments = @($Solution, '-c', $Configuration, '--logger', 'trx', '--results-directory', $Results)

if ($NoBuild) { $arguments += '--no-build' }
if ($Filter) { $arguments += @('--filter', $Filter) }

$failed = 0

for ($run = 1; $run -le $Repeat; $run++) {
    if ($Repeat -gt 1) { Write-Step "Run $run of $Repeat" }

    & dotnet test @arguments

    if ($LASTEXITCODE -ne 0) {
        $failed++
        Write-Host "  run $run failed" -ForegroundColor Red
    }

    # Only the first run needs to build.
    if ($run -eq 1 -and -not $NoBuild) { $arguments += '--no-build' }
}

if ($failed -gt 0) {
    throw "$failed of $Repeat runs failed. Results are in $Results."
}

Write-Step "All $Repeat run(s) passed. Results are in $Results."
