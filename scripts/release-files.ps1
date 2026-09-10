<#
.SYNOPSIS
    The single definition of which files in artifacts/ are released files.

.DESCRIPTION
    Dot-source this from any script that needs to know what a release consists of:

        . (Join-Path $PSScriptRoot 'release-files.ps1')

    It exists because the answer used to be written down twice. package.ps1 listed
    the released files positively, as the .zip and .exe it knew about, and
    verify-release.ps1 listed them negatively, as everything except the checksums
    file and the installer marker. The two descriptions disagreed the moment a
    third artifact appeared, which is how artifact-manifest.json came to be
    uploaded with no published checksum while the release gate failed on a release
    that was otherwise correct. One definition cannot disagree with itself.

    See issue #15.
#>

Set-StrictMode -Version Latest

function Get-ReleaseArtifact {
    <#
    .SYNOPSIS
        Returns the files in artifacts/ that are published as part of a release.

    .DESCRIPTION
        Two files in artifacts/ are deliberately not release artifacts:

        SHA256SUMS.txt, because a checksums file cannot record its own checksum.

        installed.marker, because it is input to the installer rather than a
        download. Its absence is what tells a portable copy that it is portable,
        so shipping it beside the ZIP would be actively wrong.

        Everything else in artifacts/ is released and must therefore be covered by
        a checksum. Subdirectories are ignored: payload/ is staging, and the files
        inside it reach users through the ZIP and the installer rather than on
        their own.

        The result depends on what exists when it is called, which is what lets one
        function serve both steps of packaging. Called before the manifest is
        written it returns the ZIP and the installer, which are the rows the
        manifest records. Called afterwards it returns those two and the manifest,
        which are the lines SHA256SUMS.txt records.

    .PARAMETER ArtifactRoot
        The artifacts directory to read.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ArtifactRoot
    )

    $notReleased = @('SHA256SUMS.txt', 'installed.marker')

    if (-not (Test-Path -LiteralPath $ArtifactRoot)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $ArtifactRoot -File |
        Where-Object { $_.Name -notin $notReleased } |
        Sort-Object Name)
}
