<#
.SYNOPSIS
    Checks the built artifacts against the allowlist before they are released.

.DESCRIPTION
    Run after package.ps1. This is the last gate before a release: it confirms the
    portable ZIP contains exactly what it should, that nothing which must never
    ship has crept in, that the ZIP omits installed.marker (its absence is what
    identifies a portable copy), and that every checksum matches.
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'

$ExpectedInZip = @(
    'MouseJiggler.exe',
    'MouseJiggler.exe.config',
    'MouseJiggler.Core.dll',
    'MouseJiggler.Windows.dll',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'README-install.txt'
)

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

Write-Host "Verifying artifacts in $Artifacts" -ForegroundColor Cyan

$zipPath = Join-Path $Artifacts "MouseJiggler-$Version-windows-x64-portable.zip"
Check (Test-Path $zipPath) 'The portable ZIP exists'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)

try {
    $names = @($zip.Entries | ForEach-Object { Split-Path $_.FullName -Leaf } | Where-Object { $_ })

    foreach ($expected in $ExpectedInZip) {
        Check ($names -contains $expected) "The ZIP contains $expected"
    }

    $unexpected = @($names | Where-Object { $_ -notin $ExpectedInZip })
    Check ($unexpected.Count -eq 0) "The ZIP contains nothing beyond the allowlist$(if ($unexpected) { ': ' + ($unexpected -join ', ') })"

    # Its absence is the only thing distinguishing a portable copy, so this one
    # matters more than it looks.
    Check ($names -notcontains 'installed.marker') 'The ZIP omits installed.marker'

    Check (@($names | Where-Object { $_ -like '*.pdb' }).Count -eq 0) 'The ZIP contains no debug symbols'
    Check (@($names | Where-Object { $_ -like '*Tests*' -or $_ -like 'xunit*' }).Count -eq 0) 'The ZIP contains no test assemblies'
    Check (@($names | Where-Object { $_ -like 'settings.json' }).Count -eq 0) 'The ZIP contains no user settings'

    # One versioned root folder, so extracting never scatters files.
    $roots = @($zip.Entries | ForEach-Object { ($_.FullName -split '/')[0] } | Sort-Object -Unique)
    Check ($roots.Count -eq 1 -and $roots[0] -eq "MouseJiggler-$Version") 'The ZIP extracts to one versioned folder'
}
finally {
    $zip.Dispose()
}

Write-Host 'Checking recorded checksums' -ForegroundColor Cyan
$sumsPath = Join-Path $Artifacts 'SHA256SUMS.txt'
Check (Test-Path $sumsPath) 'SHA256SUMS.txt exists'

foreach ($line in Get-Content $sumsPath) {
    if ($line -match '^([0-9a-f]{64})\s+(.+)$') {
        $recorded = $Matches[1]
        $name = $Matches[2].Trim()
        $file = Join-Path $Artifacts $name

        if (Test-Path $file) {
            $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
            Check ($actual -eq $recorded) "The checksum matches for $name"
        }
        else {
            Check $false "The file named in SHA256SUMS.txt exists: $name"
        }
    }
}

$exePath = Join-Path $Artifacts 'payload\MouseJiggler.exe'
if (Test-Path $exePath) {
    Write-Host 'Checking executable identity' -ForegroundColor Cyan
    $info = (Get-Item $exePath).VersionInfo
    Check ($info.ProductName -eq 'Mouse Jiggler') 'The product name matches'
    Check ($info.FileVersion -like "$Version*") 'The file version matches the package version'

    # Unsigned is the deliberate v1 decision, and it is disclosed rather than hidden.
    $signature = Get-AuthenticodeSignature $exePath
    Write-Host "  NOTE  Signing status: $($signature.Status). Releases are unsigned by design and published with checksums." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# The binaries inside the ZIP have to be the ones that were built, byte for byte.
# Compression and installer wrapping must not be able to alter them, and the only
# way to know that is to compare the hashes rather than assume it.
# ---------------------------------------------------------------------------
Write-Host 'Comparing the packaged binaries with the build output' -ForegroundColor Cyan

$buildOutput = Join-Path $RepoRoot 'src\MouseJiggler.App\bin\Release\net48'
$extracted = Join-Path ([System.IO.Path]::GetTempPath()) ("mousejiggler-verify-" + [Guid]::NewGuid().ToString('N'))

try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extracted)
    $extractedRoot = Join-Path $extracted "MouseJiggler-$Version"

    foreach ($binary in @('MouseJiggler.exe', 'MouseJiggler.Core.dll', 'MouseJiggler.Windows.dll')) {
        $built = Join-Path $buildOutput $binary
        $packaged = Join-Path $extractedRoot $binary

        if ((Test-Path $built) -and (Test-Path $packaged)) {
            $builtHash = (Get-FileHash $built -Algorithm SHA256).Hash
            $packagedHash = (Get-FileHash $packaged -Algorithm SHA256).Hash
            Check ($builtHash -eq $packagedHash) "$binary in the ZIP is the binary that was built"
        }
        else {
            Check $false "$binary is present in both the build output and the ZIP"
        }
    }

    # ---------------------------------------------------------------------------
    # And it has to run from wherever it was extracted to. A portable copy has no
    # installer to create anything for it, so a missing dependency or a path
    # assumption shows up here and nowhere else.
    # ---------------------------------------------------------------------------
    Write-Host 'Running the extracted portable copy' -ForegroundColor Cyan

    $portableExe = Join-Path $extractedRoot 'MouseJiggler.exe'

    if (Test-Path $portableExe) {
        # An unknown argument exits 2 without starting an engine, which is enough to
        # prove the process loads and runs without touching the user's settings.
        $probe = Start-Process $portableExe -ArgumentList '--not-a-real-argument' -Wait -PassThru -WindowStyle Hidden
        Check ($probe.ExitCode -eq 2) 'The extracted executable runs and reports an unknown argument'

        # A portable copy is identified by the absence of the installed marker, and
        # that absence is what keeps it from claiming to be an installation.
        Check (-not (Test-Path (Join-Path $extractedRoot 'installed.marker'))) 'The extracted copy identifies as portable'
    }
    else {
        Check $false 'The extracted ZIP contains a runnable executable'
    }
}
finally {
    if (Test-Path $extracted) { Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All package checks passed.' -ForegroundColor Green
exit 0
