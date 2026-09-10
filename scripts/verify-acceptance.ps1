<#
.SYNOPSIS
    Runs the rows of the issue #14 matrix that this kind of machine can answer.

.DESCRIPTION
    Most of that matrix needs hardware or an environment a developer machine does
    not have: three more Windows builds, a laptop battery, a battery-less desktop,
    mixed-DPI monitors, Narrator, a second signed-in session, and a disposable
    desktop for the rows that inject pointer input. Those stay not run, and
    docs/acceptance/v1.0.0.md says so.

    Several rows need none of that. They were being left not run alongside the rest
    purely because they sat in the same table, which is a worse answer than running
    them. This covers those.

    Nothing here injects input, restarts Explorer, or touches the pointer. It runs
    real processes and the real installer, so it is slower and more invasive than
    the unit suite: it stops running copies of the app and it uses the real
    %LOCALAPPDATA%\MouseJiggler directory, which it saves and puts back.

    Covered: A19 (one engine per session across distributions), A21 (a damaged
    config file), A23 in part (a startup entry disabled in Windows survives an
    upgrade), A25 (no network at runtime).
#>

[CmdletBinding()]
param(
    [string] $Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repoRoot 'artifacts'
$zip = Join-Path $artifacts "MouseJiggler-$Version-windows-x64-portable.zip"
$setup = Join-Path $artifacts "MouseJiggler-$Version-windows-x64-setup.exe"

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\MouseJiggler'
$exe = Join-Path $appDir 'MouseJiggler.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'MouseJiggler'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'

$failures = New-Object System.Collections.Generic.List[string]

function Check([bool] $ok, [string] $description) {
    if ($ok) {
        Write-Host "  PASS  $description" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $description" -ForegroundColor Red
        $failures.Add($description)
    }
}

function Step([string] $message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Stop-EveryCopy {
    Get-Process MouseJiggler -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Uninstall-Silently {
    $uninstaller = Join-Path $appDir 'unins000.exe'
    if (Test-Path $uninstaller) {
        Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        for ($i = 0; $i -lt 60 -and (Test-Path $appDir); $i++) { Start-Sleep -Milliseconds 500 }
    }
}

function Expand-Portable {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('mj-acceptance-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $root)
    return (Join-Path $root "MouseJiggler-$Version\MouseJiggler.exe")
}

foreach ($required in @($zip, $setup)) {
    if (-not (Test-Path $required)) {
        throw "$required is missing. Run scripts/package.ps1 first."
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Whatever is already here belongs to whoever is running this, and it is put back.
$savedData = $null
if (Test-Path $dataDir) {
    $savedData = Join-Path ([System.IO.Path]::GetTempPath()) ('mj-savedata-' + [Guid]::NewGuid().ToString('N'))
    Copy-Item $dataDir $savedData -Recurse
}

Stop-EveryCopy
if (Test-Path $appDir) { Uninstall-Silently }
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }

try {
    # -----------------------------------------------------------------------
    Step 'A19  One engine per user and session, across both distributions'
    # -----------------------------------------------------------------------
    $portableExe = Expand-Portable
    $otherExe = Expand-Portable

    $first = Start-Process $portableExe -ArgumentList '--startup' -PassThru
    Start-Sleep -Seconds 4
    Check (-not $first.HasExited) 'the first copy is running'

    # A quiet duplicate leaves without a word rather than stealing focus at sign-in.
    $duplicate = Start-Process $portableExe -ArgumentList '--startup' -PassThru -Wait
    Check ($duplicate.ExitCode -eq 0) 'a duplicate sign-in launch exits quietly'

    # A copy at a different path is still the same user and session, so it must not
    # start a second engine either. Two engines would mean two components writing one
    # settings file and holding two wake requests.
    $other = Start-Process $otherExe -ArgumentList '--startup' -PassThru -Wait
    Check ($other.ExitCode -eq 0) 'a copy at another path does not start a second engine'

    Start-Sleep -Seconds 1
    $running = @(Get-Process MouseJiggler -ErrorAction SilentlyContinue)
    Check ($running.Count -eq 1) "exactly one engine is running (found $($running.Count))"

    $first.Refresh()
    Check (-not $first.HasExited) 'the original engine is the one still running'

    # -----------------------------------------------------------------------
    Step 'A25  No network connections at runtime'
    # -----------------------------------------------------------------------
    $connections = @()
    for ($sample = 0; $sample -lt 6; $sample++) {
        $connections += @(Get-NetTCPConnection -OwningProcess $first.Id -ErrorAction SilentlyContinue)
        $connections += @(Get-NetUDPEndpoint -OwningProcess $first.Id -ErrorAction SilentlyContinue)
        Start-Sleep -Seconds 1
    }

    Check ($connections.Count -eq 0) "the running app opened no sockets (found $($connections.Count))"

    Stop-EveryCopy

    # -----------------------------------------------------------------------
    Step 'A21  A damaged configuration file'
    # -----------------------------------------------------------------------
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $settingsPath = Join-Path $dataDir 'settings.json'

    # Truncated mid-object: valid JSON up to the point where it stops.
    $damaged = '{"schemaVersion":1,"revision":9,"stopped":false,"runMode":"Man'
    Set-Content -Path $settingsPath -Value $damaged -NoNewline
    $damagedBytes = [System.IO.File]::ReadAllBytes($settingsPath)

    $recovering = Start-Process $portableExe -ArgumentList '--startup' -PassThru
    Start-Sleep -Seconds 5

    Check (-not $recovering.HasExited) 'the app runs rather than crashing on a damaged file'

    $afterBytes = [System.IO.File]::ReadAllBytes($settingsPath)
    Check ([System.Linq.Enumerable]::SequenceEqual($damagedBytes, $afterBytes)) 'the damaged file was left exactly as it was'

    # Recovery is an explicit action. Quarantining on sight would move the evidence
    # before anyone had chosen to.
    $quarantined = @(Get-ChildItem $dataDir -Filter 'settings.invalid-*.json' -ErrorAction SilentlyContinue)
    Check ($quarantined.Count -eq 0) 'nothing was quarantined without being asked'

    # A file that says stopped=false must not become activity when it cannot be read.
    Check (-not (Test-Path (Join-Path $dataDir 'settings.json.bak'))) 'no backup was written over a file that was never read'

    Stop-EveryCopy
    Remove-Item $dataDir -Recurse -Force

    # -----------------------------------------------------------------------
    Step 'A23  A startup entry disabled in Windows survives an upgrade'
    # -----------------------------------------------------------------------
    Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=startup' -Wait
    Check (Test-Path $exe) 'the installed copy is present'

    $runValue = (Get-ItemProperty $runKey -Name 'MouseJiggler' -ErrorAction SilentlyContinue).MouseJiggler
    Check ($null -ne $runValue) 'the startup entry was written'

    # What Windows writes when the user switches the app off in Settings or Task
    # Manager. It is undocumented, which is exactly why the app must never write it;
    # planting it here is how this test stands in for the user having done so.
    if (-not (Test-Path $approvedKey)) { New-Item -Path $approvedKey -Force | Out-Null }
    $disabledBytes = [byte[]] (3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
    Set-ItemProperty -Path $approvedKey -Name 'MouseJiggler' -Value $disabledBytes -Type Binary

    Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep -Seconds 2

    $approvedAfter = (Get-ItemProperty $approvedKey -Name 'MouseJiggler' -ErrorAction SilentlyContinue).MouseJiggler
    Check ($null -ne $approvedAfter -and [System.Linq.Enumerable]::SequenceEqual([byte[]] $disabledBytes, [byte[]] $approvedAfter)) 'the upgrade left the Windows startup decision alone'

    $runAfter = (Get-ItemProperty $runKey -Name 'MouseJiggler' -ErrorAction SilentlyContinue).MouseJiggler
    Check ($runAfter -eq $runValue) 'the upgrade did not rewrite the Run value'

    # The app itself must not undo it either, on an ordinary launch.
    $launched = Start-Process $exe -ArgumentList '--startup' -PassThru
    Start-Sleep -Seconds 4
    Stop-EveryCopy

    $approvedAfterLaunch = (Get-ItemProperty $approvedKey -Name 'MouseJiggler' -ErrorAction SilentlyContinue).MouseJiggler
    Check ($null -ne $approvedAfterLaunch -and [System.Linq.Enumerable]::SequenceEqual([byte[]] $disabledBytes, [byte[]] $approvedAfterLaunch)) 'launching the app left the Windows startup decision alone'
}
finally {
    Stop-EveryCopy
    if (Test-Path $appDir) { Uninstall-Silently }

    Remove-ItemProperty -Path $approvedKey -Name 'MouseJiggler' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name 'MouseJiggler' -ErrorAction SilentlyContinue

    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($savedData) {
        Copy-Item $savedData $dataDir -Recurse -ErrorAction SilentlyContinue
        Remove-Item $savedData -Recurse -Force -ErrorAction SilentlyContinue
    }

    Get-ChildItem ([System.IO.Path]::GetTempPath()) -Filter 'mj-acceptance-*' -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host 'Every acceptance check that this machine can answer passed.' -ForegroundColor Green
