<#
.SYNOPSIS
    Puts the pinned Inno Setup compiler on this machine, having checked it first.

.DESCRIPTION
    Issue #13 asks for the installer compiler to be fetched only from its upstream
    release, with the recorded SHA-256 and the publisher signature verified before
    anything is executed. Nothing was doing that. tool-versions.json recorded a URL,
    a hash, a size and an expected signer, and no code read any of them, so a release
    build on a fresh runner produced no installer at all: package.ps1 looked for a
    compiler, did not find one, printed a warning and carried on.

    Everything here happens before the downloaded file is run. A hash that does not
    match, a signature that is not valid, or a signer that is not the recorded one
    all stop the script. There is no switch to skip the checks, because a switch to
    skip them is how they come to be skipped.

.PARAMETER Force
    Reinstall even when the pinned version is already present.
#>

[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path $PSScriptRoot -Parent
$pinned = Get-Content (Join-Path $PSScriptRoot 'tool-versions.json') -Raw | ConvertFrom-Json
$inno = $pinned.innoSetup

function Write-Step([string] $message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Get-InstalledInnoVersion {
    # The compiler's own files carry no version resource: ISCC.exe reports 0.0.0.0 and
    # its banner names no version either. The uninstall entry its own installer writes
    # is the one place the version is actually recorded.
    foreach ($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
                        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (-not (Test-Path $root)) { continue }

        foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $entry = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($entry.PSObject.Properties.Name -notcontains 'DisplayName') { continue }
            if ($entry.DisplayName -notlike 'Inno Setup version*') { continue }

            return $entry.DisplayVersion
        }
    }

    return $null
}

function Find-Compiler {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$installed = Get-InstalledInnoVersion

if (-not $Force -and $installed -eq $inno.version -and (Find-Compiler)) {
    Write-Host "Inno Setup $($inno.version) is already installed at $(Find-Compiler)." -ForegroundColor Green
    exit 0
}

if ($installed -and $installed -ne $inno.version) {
    # A different version compiles the script differently. Saying so beats silently
    # producing an installer nobody pinned.
    Write-Warning "Inno Setup $installed is installed, but $($inno.version) is pinned. Installing the pinned version over it."
}

Write-Step "Downloading Inno Setup $($inno.version)"
Write-Host "  from $($inno.url)"

$download = Join-Path ([System.IO.Path]::GetTempPath()) "innosetup-$($inno.version)-$([Guid]::NewGuid().ToString('N')).exe"

try {
    # TLS 1.2 is not the default on the Windows PowerShell that some runners still
    # provide, and the failure it produces otherwise looks like a network fault.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $inno.url -OutFile $download -UseBasicParsing

    Write-Step 'Verifying what was downloaded'

    $actualSize = (Get-Item $download).Length
    if ($actualSize -ne $inno.sizeBytes) {
        throw "Expected $($inno.sizeBytes) bytes but got $actualSize. Refusing to run it."
    }
    Write-Host "  size    $actualSize bytes" -ForegroundColor Green

    $actualHash = (Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $inno.sha256.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 is $actualHash but $expectedHash was recorded. Refusing to run it."
    }
    Write-Host "  sha256  $actualHash" -ForegroundColor Green

    # The hash pins the bytes to what was reviewed. The signature is what says those
    # bytes came from the publisher rather than from whoever could reach the recorded
    # URL, so both are checked and neither substitutes for the other.
    $signature = Get-AuthenticodeSignature $download

    if ($signature.Status -ne 'Valid') {
        throw "The Authenticode signature is $($signature.Status). Refusing to run it."
    }

    $subject = $signature.SignerCertificate.Subject
    if ($subject -ne $inno.expectedSigner) {
        throw "Signed by '$subject' rather than the recorded '$($inno.expectedSigner)'. Refusing to run it."
    }

    Write-Host "  signer  $subject" -ForegroundColor Green

    Write-Step 'Installing'

    # Per-user, matching everything else this repository does, and so no elevation is
    # needed on a developer machine or a runner.
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER')
    $process = Start-Process $download -ArgumentList $arguments -PassThru -Wait

    if ($process.ExitCode -ne 0) {
        throw "The Inno Setup installer exited with code $($process.ExitCode)."
    }
}
finally {
    Remove-Item $download -Force -ErrorAction SilentlyContinue
}

$compiler = Find-Compiler
$now = Get-InstalledInnoVersion

if (-not $compiler) {
    throw 'Inno Setup reported success but no ISCC.exe was found.'
}

if ($now -ne $inno.version) {
    throw "Inno Setup reports version $now after installing $($inno.version)."
}

Write-Host ''
Write-Host "Inno Setup $($inno.version) is installed at $compiler." -ForegroundColor Green
