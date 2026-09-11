<#
.SYNOPSIS
    Builds the release artifacts: the portable ZIP, the installer, the artifact
    manifest, and SHA256SUMS.txt covering all of them.

.DESCRIPTION
    Everything shipped is named explicitly in $PayloadFiles. An allowlist rather
    than a copy of the output folder is what keeps test assemblies, PDBs and
    development leftovers out of a release, and it fails loudly when an expected
    file is missing instead of shipping an incomplete package.

    The installer step needs Inno Setup at the version pinned in
    scripts/tool-versions.json. scripts/install-inno.ps1 fetches it from the
    upstream release and verifies its hash and publisher signature before running
    it. Without a compiler this script still produces the portable ZIP and says
    plainly that the installer was skipped, unless -RequireInstaller says that a
    package without one is not a package.
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    [string] $Configuration = 'Release',
    [switch] $SkipBuild,

    # Fail rather than warn when the installer cannot be built. The release workflow
    # sets this, because a build that produces only the ZIP looks entirely successful
    # right up to the point where somebody goes looking for the installer.
    [switch] $RequireInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# One definition of which files in artifacts/ are released, shared with
# verify-release.ps1 so the two cannot drift apart again. See issue #15.
. (Join-Path $PSScriptRoot 'release-files.ps1')

$RepoRoot = Split-Path -Parent $PSScriptRoot
$BuildOutput = Join-Path $RepoRoot 'src\MouseJiggler.App\bin' | Join-Path -ChildPath $Configuration | Join-Path -ChildPath 'net48'
$Artifacts = Join-Path $RepoRoot 'artifacts'
$Payload = Join-Path $Artifacts 'payload'

# Exactly what ships. Anything else in the build output is not a release file.
$PayloadFiles = @(
    'MouseJiggler.exe',
    'MouseJiggler.exe.config',
    'MouseJiggler.Core.dll',
    'MouseJiggler.Windows.dll',
    'LICENSE'
)

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

if (-not $SkipBuild) {
    Write-Step 'Restoring and building'
    & dotnet restore (Join-Path $RepoRoot 'MouseJiggler.sln') --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    & dotnet build (Join-Path $RepoRoot 'MouseJiggler.sln') -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

Write-Step 'Assembling the payload'
if (Test-Path $Artifacts) { Remove-Item $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $Payload -Force | Out-Null

foreach ($file in $PayloadFiles) {
    $source = Join-Path $BuildOutput $file
    if (-not (Test-Path $source)) {
        throw "Missing payload file: $file. The package is incomplete."
    }

    Copy-Item $source -Destination (Join-Path $Payload $file)
}

Copy-Item (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md') -Destination $Payload
Copy-Item (Join-Path $RepoRoot 'docs\README-install.txt') -Destination $Payload

# The marker ships only inside the installer. The ZIP deliberately omits it,
# which is how a portable copy knows not to touch startup registration.
Set-Content -Path (Join-Path $Artifacts 'installed.marker') -Value "schemaVersion=1`nstartupDefault=true" -NoNewline

Write-Step 'Checking for files that must never ship'
$forbidden = Get-ChildItem $Payload -File | Where-Object {
    $_.Extension -in @('.pdb', '.xml') -or $_.Name -like '*Tests*' -or $_.Name -like 'xunit*'
}

if ($forbidden) {
    throw "Files that must not ship were found in the payload: $($forbidden.Name -join ', ')"
}

Write-Step 'Building the portable ZIP'
$zipName = "MouseJiggler-$Version-windows-x64-portable.zip"
$zipRoot = Join-Path $Artifacts "MouseJiggler-$Version"
New-Item -ItemType Directory -Path $zipRoot -Force | Out-Null
Copy-Item (Join-Path $Payload '*') -Destination $zipRoot -Recurse

Compress-Archive -Path $zipRoot -DestinationPath (Join-Path $Artifacts $zipName) -CompressionLevel Optimal
Remove-Item $zipRoot -Recurse -Force

Write-Step 'Building the installer'
$toolVersions = Get-Content (Join-Path $PSScriptRoot 'tool-versions.json') -Raw | ConvertFrom-Json
$innoVersion = $toolVersions.innoSetup.version

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc (Join-Path $RepoRoot 'installer\MouseJiggler.iss') "/DAppVersion=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }
}
elseif ($RequireInstaller) {
    # A release that quietly ships without its installer is worse than one that fails.
    # The download is verified before it runs, so there is no reason to fetch it here
    # implicitly: say what is missing and how to get it.
    throw "Inno Setup $innoVersion was not found and an installer was required. Run scripts/install-inno.ps1 first."
}
else {
    Write-Warning "Inno Setup $innoVersion was not found, so no installer was built. The portable ZIP is still complete."
}

Write-Step 'Writing the artifact manifest'

# The manifest used to be written by the release workflow, after this script had
# already produced SHA256SUMS.txt. That ordering left the manifest as the one
# uploaded file with no published checksum. Writing it here, before the checksums,
# is what lets SHA256SUMS.txt cover it. See issue #15.
$commit = & git -C $RepoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
    throw 'The source commit could not be read. The manifest records provenance, so a package without it is not a package.'
}

$sdk = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdk)) {
    throw 'The build SDK version could not be read.'
}

$manifestRows = Get-ReleaseArtifact -ArtifactRoot $Artifacts |
    ForEach-Object {
        [pscustomobject]@{
            name   = $_.Name
            bytes  = $_.Length
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            commit = $commit.Trim()
            sdk    = $sdk.Trim()
        }
    }

# -InputObject rather than the pipeline: piping unrolls a single-element array, and
# Windows PowerShell then writes a bare object where every reader expects a list.
$manifestJson = ConvertTo-Json -InputObject @($manifestRows) -Depth 3
Set-Content -Path (Join-Path $Artifacts 'artifact-manifest.json') -Value $manifestJson -Encoding ascii

Write-Step 'Writing SHA256SUMS.txt'

# Last, and nothing may write into artifacts/ after it. Every file the helper
# returns is uploaded, so every file the helper returns gets a line here, the
# manifest included.
$sumsPath = Join-Path $Artifacts 'SHA256SUMS.txt'
$lines = Get-ReleaseArtifact -ArtifactRoot $Artifacts |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }

Set-Content -Path $sumsPath -Value $lines -Encoding ascii

Write-Step 'Done'
Get-ChildItem $Artifacts -File | Select-Object Name, Length | Format-Table -AutoSize
Get-Content $sumsPath
