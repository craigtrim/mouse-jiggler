<#
.SYNOPSIS
    The final gate before publishing a release.

.DESCRIPTION
    verify-package.ps1 checks the contents of the artifacts. This checks the
    release around them: that the build is reproducible, that the version stamped
    into the executable matches the one being released, that the checksums file
    covers every artifact and nothing else, and that the tag and the working tree
    agree with what is being shipped.

    It reports every failure rather than stopping at the first, because finding
    out about one problem at a time is how a release takes an afternoon.
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    [string] $Configuration = 'Release',
    [switch] $SkipReproducibility
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'
$Output = Join-Path $RepoRoot "src/MouseJiggler.App/bin/$Configuration/net48"

$failures = New-Object System.Collections.Generic.List[string]

function Check([bool] $ok, [string] $description) {
    if ($ok) {
        Write-Host "  PASS  $description" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $description" -ForegroundColor Red
        $script:failures.Add($description)
    }
}

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

Write-Step 'Artifacts'

$zip = Join-Path $Artifacts "MouseJiggler-$Version-windows-x64-portable.zip"
$setup = Join-Path $Artifacts "MouseJiggler-$Version-windows-x64-setup.exe"
$sums = Join-Path $Artifacts 'SHA256SUMS.txt'

Check (Test-Path $zip) 'the portable ZIP exists'
Check (Test-Path $setup) 'the installer exists'
Check (Test-Path $sums) 'SHA256SUMS.txt exists'

if (Test-Path $sums) {
    Write-Step 'Checksums'

    $listed = @{}
    foreach ($line in Get-Content $sums) {
        if ($line -match '^([0-9a-fA-F]{64})\s+(.+)$') {
            $listed[$Matches[2].Trim()] = $Matches[1].ToUpperInvariant()
        }
    }

    $released = @(Get-ChildItem $Artifacts -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Name -ne 'installed.marker' })

    foreach ($file in $released) {
        if (-not $listed.ContainsKey($file.Name)) {
            Check $false "$($file.Name) is listed in SHA256SUMS.txt"
            continue
        }

        $actual = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        Check ($actual -eq $listed[$file.Name]) "$($file.Name) matches its checksum"
    }

    # A name in the checksums file that is not on disk means the release is being
    # published with a hash for something nobody can download.
    foreach ($name in $listed.Keys) {
        Check (Test-Path (Join-Path $Artifacts $name)) "$name named in SHA256SUMS.txt is present"
    }
}

Write-Step 'Version stamp'

if (Test-Path (Join-Path $Output 'MouseJiggler.exe')) {
    $info = (Get-Item (Join-Path $Output 'MouseJiggler.exe')).VersionInfo
    $stamped = $info.FileVersion

    # The stamp carries four parts; the release version carries three.
    Check ($stamped -like "$Version*") "the executable reports $Version (it reports $stamped)"
}
else {
    Check $false 'the built executable exists'
}

Write-Step 'Application icon'

if (Test-Path (Join-Path $Output 'MouseJiggler.exe')) {
    Add-Type -AssemblyName System.Drawing
    $icon = $null

    try {
        $icon = [System.Drawing.Icon]::ExtractAssociatedIcon((Join-Path $Output 'MouseJiggler.exe'))
        Check ($null -ne $icon -and $icon.Width -gt 0) 'the executable carries an icon'
    }
    catch {
        Check $false 'the executable carries an icon'
    }
    finally {
        if ($null -ne $icon) { $icon.Dispose() }
    }
}

if (-not $SkipReproducibility) {
    Write-Step 'Reproducible build'

    # Two clean builds, compared with each other. Comparing whatever happens to be
    # in the output directory against a clean rebuild is not the same question: an
    # incremental build can differ from a clean one for reasons that have nothing
    # to do with determinism, and that is exactly the state a developer is in when
    # they run this. The first version of this check reported the build as not
    # reproducible when it demonstrably was, which is worse than not checking:
    # a false alarm before a release either blocks a good one or teaches whoever
    # sees it to ignore the check.
    $pattern = Join-Path $Output '*'

    function Invoke-CleanBuild {
        $stale = @(Get-ChildItem (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests') -Include bin, obj -Recurse -Directory)
        foreach ($directory in $stale) { Remove-Item $directory.FullName -Recurse -Force }

        & dotnet restore (Join-Path $RepoRoot 'MouseJiggler.sln') --locked-mode | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed during the reproducibility check.' }

        & dotnet build (Join-Path $RepoRoot 'MouseJiggler.sln') -c $Configuration --no-restore | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Build failed during the reproducibility check.' }

        $hashes = @{}
        foreach ($file in Get-ChildItem $pattern -File -Include *.dll, *.exe) {
            $hashes[$file.Name] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        }

        return $hashes
    }

    $first = Invoke-CleanBuild

    if ($first.Count -eq 0) {
        throw "Nothing was found to compare in $Output."
    }

    $second = Invoke-CleanBuild

    foreach ($name in $first.Keys) {
        if (-not $second.ContainsKey($name)) {
            Check $false "$name is present in both builds"
            continue
        }

        # The executable is included, not just the libraries. It is what people
        # download, and it carries the embedded icon resource that PathMap cannot
        # reach inside.
        Check ($first[$name] -eq $second[$name]) "$name rebuilt identically"
    }
}

Write-Step 'Working tree'

Push-Location $RepoRoot
try {
    $dirty = @(& git status --porcelain)
    Check ($dirty.Count -eq 0) 'the working tree is clean'

    $tag = & git tag --points-at HEAD
    Check ($null -ne $tag -and $tag -contains "v$Version") "HEAD is tagged v$Version"
}
finally {
    Pop-Location
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host 'Every release check passed.' -ForegroundColor Green
