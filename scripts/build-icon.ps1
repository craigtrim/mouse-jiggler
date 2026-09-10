<#
.SYNOPSIS
    Regenerates assets/MouseJiggler.ico from the artwork drawn in TrayIconFactory.

.DESCRIPTION
    The shapes are drawn in code, so the application icon has to be generated from them
    rather than maintained as a separate file that can silently drift from the tray.

    The result is committed, because <ApplicationIcon> needs the file to exist before the
    build that produces the assembly this script loads. Run it only when the artwork
    changes; IconFileTests fails if the committed file stops matching the code.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [ValidateSet('Stopped', 'Running', 'Waiting', 'Error')]
    [string] $Shape = 'Running'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$assembly = Join-Path $root "src/MouseJiggler.App/bin/$Configuration/net48/MouseJiggler.exe"

if (-not (Test-Path $assembly)) {
    throw "Build the app first: dotnet build -c $Configuration. Expected $assembly"
}

$assetsDirectory = Join-Path $root 'assets'
if (-not (Test-Path $assetsDirectory)) {
    New-Item -ItemType Directory -Path $assetsDirectory | Out-Null
}

$target = Join-Path $assetsDirectory 'MouseJiggler.ico'

# Loading a copy leaves the original free for the build that follows.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("mousejiggler-icon-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    Copy-Item (Join-Path (Split-Path $assembly) '*.dll') $staging -ErrorAction SilentlyContinue
    Copy-Item $assembly $staging

    $loaded = [System.Reflection.Assembly]::LoadFrom((Join-Path $staging 'MouseJiggler.exe'))
    $writer = $loaded.GetType('MouseJiggler.App.IconFileWriter')
    $factory = $loaded.GetType('MouseJiggler.App.TrayIconFactory')

    $shapeValue = [Enum]::Parse($factory.GetNestedType('Shape'), $Shape)
    $sizes = $factory.GetProperty('StandardSizes').GetValue($null)

    $arguments = [object[]]::new(3)
    $arguments[0] = $shapeValue
    $arguments[1] = $sizes
    $arguments[2] = [string]$target
    $writer.GetMethod('WriteFile').Invoke($null, $arguments)

    $written = Get-Item $target
    Write-Output ("Wrote {0} ({1} bytes) from the {2} shape at {3}" -f $target, $written.Length, $Shape, ($sizes -join ', '))
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
