<#
.SYNOPSIS
    Restores and builds the solution exactly as CI does.

.DESCRIPTION
    One command that matches the CI job step for step, so a green run here means
    the same thing as a green run there. The restore is locked: a package version
    that has drifted from packages.lock.json fails rather than being resolved to
    something newer that nobody reviewed.

    Warnings are errors across the whole solution, set in Directory.Build.props,
    so there is no separate strictness switch here.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot 'MouseJiggler.sln'

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

Write-Step 'Toolchain'
& dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'The .NET SDK is not on PATH.' }

# global.json pins the SDK with rollForward disabled, so a mismatch here is a
# genuine problem rather than a detail: the pinned SDK is part of the build.
& dotnet --list-sdks

if ($Clean) {
    Write-Step 'Removing previous output'
    $stale = @(Get-ChildItem (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests') -Include bin, obj -Recurse -Directory)
    foreach ($directory in $stale) {
        Remove-Item $directory.FullName -Recurse -Force
    }
    Write-Host "  removed $($stale.Count) directories"
}

Write-Step 'Restoring with the lock file enforced'
& dotnet restore $Solution --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed. If a package version changed on purpose, update packages.lock.json deliberately.' }

Write-Step "Building $Configuration"
& dotnet build $Solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Step 'Checking the shipped payload'

# An allowlist rather than an inspection of what happens to be there. A
# third-party assembly appearing in this folder would mean the application had
# gained a runtime dependency it is not allowed to have.
$allowed = @(
    'MouseJiggler.exe',
    'MouseJiggler.exe.config',
    'MouseJiggler.Core.dll',
    'MouseJiggler.Windows.dll',
    'LICENSE'
)

$output = Join-Path $RepoRoot "src/MouseJiggler.App/bin/$Configuration/net48"
$unexpected = @(Get-ChildItem $output -File | Where-Object { $_.Name -notin $allowed })

if ($unexpected.Count -gt 0) {
    throw "Unexpected files in the app output: $($unexpected.Name -join ', ')"
}

Write-Host "  only the owned assemblies are present"
Write-Step 'Done'
