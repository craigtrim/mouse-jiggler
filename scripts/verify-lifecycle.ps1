<#
.SYNOPSIS
    Drives the whole install lifecycle against the real installer and reports what happened.

.DESCRIPTION
    Covers the sequences from issue #12 that can be checked on one machine: silent
    install without elevation, the startup task, running the app, upgrading over a
    running copy, portable-to-installed settings adoption, and uninstall.

    It also checks the things that are easy to get wrong in the other direction:
    that an unrelated Run value and an unrelated file in the data directory are
    both still there afterwards. An uninstaller that removes slightly too much is
    worse than one that leaves a stray key, because the damage is somebody else's.

    Every check reports rather than throwing, so one failure does not hide the
    rest. The script leaves the machine as it found it.
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$setup = Join-Path $root "artifacts/MouseJiggler-$Version-windows-x64-setup.exe"
$zip = Join-Path $root "artifacts/MouseJiggler-$Version-windows-x64-portable.zip"

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\MouseJiggler'
$exe = Join-Path $appDir 'MouseJiggler.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'MouseJiggler'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

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

function Step([string] $message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Get-RunValue([string] $name) {
    $key = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
    if ($null -eq $key) { return $null }
    $property = $key.PSObject.Properties | Where-Object { $_.Name -eq $name }
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Wait-ForRemoval([string] $path, [int] $seconds = 30) {
    for ($i = 0; $i -lt $seconds -and (Test-Path $path); $i++) { Start-Sleep -Seconds 1 }
}

function Uninstall-Silently {
    $uninstaller = Get-ChildItem $appDir -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $uninstaller) { return }

    Start-Process $uninstaller.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Wait-ForRemoval $exe
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $setup)) { throw "Build the installer first: scripts/package.ps1. Expected $setup" }
if (-not (Test-Path $zip)) { throw "Build the portable ZIP first: scripts/package.ps1. Expected $zip" }

# Leave nothing from a previous run to be mistaken for a result of this one.
if (Test-Path $appDir) { Uninstall-Silently }
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }

# Something that belongs to somebody else, planted where an over-eager uninstaller
# would find it.
$bystanderName = 'MouseJigglerLifecycleBystander'
Set-ItemProperty -Path $runKey -Name $bystanderName -Value 'C:\Windows\System32\notepad.exe'

try {
    # -----------------------------------------------------------------------
    Step 'A portable copy runs first, and saves a preference'
    # -----------------------------------------------------------------------
    $portableRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mousejiggler-portable-" + [Guid]::NewGuid().ToString('N'))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $portableRoot)
    $portableExe = Join-Path $portableRoot "MouseJiggler-$Version\MouseJiggler.exe"

    Check (Test-Path $portableExe) 'the portable copy extracted'

    # A distinctive interval, so it is obvious later whether the settings survived.
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $portableSettings = '{"schemaVersion":1,"revision":4,"stopped":true,"runMode":"Manual",' +
        '"scheduleEnabled":false,"scheduleStart":"08:00","scheduleEnd":"17:00","dayMask":127,' +
        '"pauseOnBattery":true,"keepDisplayOn":true,"jiggleMouse":true,"intervalSeconds":115,' +
        '"diagnosticLogging":false,"startupInitialized":true,"firstRunCompleted":true}'
    Set-Content -Path (Join-Path $dataDir 'settings.json') -Value $portableSettings -NoNewline

    $portable = Start-Process $portableExe -ArgumentList '--startup' -PassThru
    Start-Sleep -Seconds 4
    Check (-not $portable.HasExited) 'the portable copy runs'

    # A copy at a different path may not close this one. The check behind that refusal
    # asks the kernel which process is really answering the pipe rather than believing
    # the path in its reply, and it only works across two real processes: an
    # in-process test server keeps its pipe instance alive long enough that a query
    # made at the wrong moment still succeeds, so this is the only place the ordering
    # is actually exercised.
    $imposterRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mousejiggler-imposter-" + [Guid]::NewGuid().ToString('N'))
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $imposterRoot)
    $imposterExe = Join-Path $imposterRoot "MouseJiggler-$Version\MouseJiggler.exe"

    $refused = Start-Process $imposterExe -ArgumentList '--shutdown-for-update' -PassThru -Wait
    Check ($refused.ExitCode -eq 3) 'a copy at another path is refused with an ownership conflict'

    $portable.Refresh()
    Check (-not $portable.HasExited) 'the refused request left the running copy alone'

    # The same command from the same path is the legitimate one, and it has to work.
    # This is what fails when the owner is identified after its reply instead of on
    # connection: the owner closes the pipe as soon as it answers, the query comes back
    # ERROR_PIPE_NOT_CONNECTED, and every honest caller is refused as an impostor.
    $shutdown = Start-Process $portableExe -ArgumentList '--shutdown-for-update' -PassThru -Wait
    Check ($shutdown.ExitCode -eq 0) 'the same copy is allowed to ask itself to close'

    Start-Sleep -Seconds 2
    $portable.Refresh()
    Check $portable.HasExited 'the portable copy exits when asked'

    # -----------------------------------------------------------------------
    Step 'Installing over it adopts the settings rather than replacing them'
    # -----------------------------------------------------------------------
    Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=startup' -Wait
    Check (Test-Path $exe) 'the installed copy is present'
    Check ($null -ne (Get-RunValue 'MouseJiggler')) 'the startup entry was written'

    $adopted = Get-Content (Join-Path $dataDir 'settings.json') -Raw
    Check ($adopted -match '"intervalSeconds":115') 'the portable settings were adopted, not overwritten'

    $installedRun = Get-RunValue 'MouseJiggler'
    Check ($installedRun -like "*$appDir*") 'the startup entry points at the installed copy'

    # -----------------------------------------------------------------------
    Step 'Upgrading over a running copy'
    # -----------------------------------------------------------------------
    $running = Start-Process $exe -ArgumentList '--startup' -PassThru
    Start-Sleep -Seconds 4
    Check (-not $running.HasExited) 'the installed copy is running before the upgrade'

    Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep -Seconds 3
    $running.Refresh()

    Check $running.HasExited 'the running copy was closed for the upgrade'
    Check (Test-Path $exe) 'the executable survived the upgrade'

    $afterUpgrade = Get-Content (Join-Path $dataDir 'settings.json') -Raw
    Check ($afterUpgrade -match '"intervalSeconds":115') 'settings survived the upgrade'
    Check ($null -ne (Get-RunValue 'MouseJiggler')) 'the startup entry survived the upgrade'

    # -----------------------------------------------------------------------
    Step 'Uninstalling'
    # -----------------------------------------------------------------------
    # Something of the user's own, in the data directory, that no uninstaller owns.
    $bystanderFile = Join-Path $dataDir 'my-own-notes.txt'
    Set-Content -Path $bystanderFile -Value 'not the installer''s to delete' -NoNewline

    Uninstall-Silently

    Check (-not (Test-Path $exe)) 'the executable was removed'
    Check (-not (Test-Path $appDir)) 'the install directory was removed'
    Check ($null -eq (Get-RunValue 'MouseJiggler')) 'the owned startup entry was removed'

    Check ($null -ne (Get-RunValue $bystanderName)) 'an unrelated startup entry was left alone'
    Check (Test-Path $bystanderFile) 'an unrelated file in the data directory was left alone'
    Check (Test-Path (Join-Path $dataDir 'settings.json')) 'a silent uninstall kept the settings'
}
finally {
    if (Test-Path $appDir) { Uninstall-Silently }
    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-ItemProperty -Path $runKey -Name $bystanderName -ErrorAction SilentlyContinue
    Get-ChildItem ([System.IO.Path]::GetTempPath()) -Filter 'mousejiggler-portable-*' -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem ([System.IO.Path]::GetTempPath()) -Filter 'mousejiggler-imposter-*' -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host 'Every lifecycle check passed.' -ForegroundColor Green
