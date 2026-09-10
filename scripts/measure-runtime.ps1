<#
.SYNOPSIS
    Measures the runtime gates from issue #14 that one machine can actually answer.

.DESCRIPTION
    This is evidence for one OS row, not the matrix. It runs the cases and
    performance gates that do not need a second monitor, a battery, a screen
    reader or a disposable desktop, and it records real numbers rather than
    impressions.

    Everything it starts, it stops. It never restarts Explorer and never injects
    input, because this may be somebody's working machine.

    Readiness is detected by the named pipe the engine creates when it takes
    ownership of the session. That is the moment the app is actually usable,
    which is what "reaches the tray" has to mean if the number is to be worth
    anything.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [int] $CpuMinutes = 15,
    [switch] $Quick
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Quick) { $CpuMinutes = 1 }

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src/MouseJiggler.App/bin/$Configuration/net48/MouseJiggler.exe"
$dataDir = Join-Path $env:LOCALAPPDATA 'MouseJiggler'

if (-not (Test-Path $exe)) { throw "Build the app first. Expected $exe" }

$results = New-Object System.Collections.Generic.List[object]

function Record([string] $id, [string] $what, [string] $measured, [bool] $ok) {
    $results.Add([pscustomobject]@{ Id = $id; Check = $what; Measured = $measured; Passed = $ok })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0}  {1,-8} {2} -- {3}" -f $mark, $id, $what, $measured) -ForegroundColor $colour
}

function Step([string] $message) { Write-Host "==> $message" -ForegroundColor Cyan }

function Get-PipeNames {
    return [System.IO.Directory]::GetFiles('\\.\pipe\') | ForEach-Object { Split-Path $_ -Leaf }
}

function Test-EngineOwned {
    return @(Get-PipeNames | Where-Object { $_ -like 'MouseJiggler.*' }).Count -gt 0
}

function Test-WakeRequestHeld {
    return ((& powercfg '/requests' | Out-String) -match 'MouseJiggler')
}

# Each write has to carry a higher revision than the last. The app compares
# revisions to decide whether a file has really changed, so rewriting the same
# number is correctly ignored and the change never arrives.
$script:revision = 1

function Write-Settings([bool] $Stopped, [bool] $KeepDisplayOn, [bool] $JiggleMouse, [string] $Start = '08:00', [string] $End = '17:00', [bool] $ScheduleEnabled = $false) {
    $flag = { param($value) if ($value) { 'true' } else { 'false' } }

    if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }

    $script:revision++

    $document = '{"schemaVersion":1,"revision":' + $script:revision + ',"stopped":' + (& $flag $Stopped) +
        ',"runMode":"Manual","scheduleEnabled":' + (& $flag $ScheduleEnabled) +
        ',"scheduleStart":"' + $Start + '","scheduleEnd":"' + $End + '"' +
        ',"dayMask":127,"pauseOnBattery":false,"keepDisplayOn":' + (& $flag $KeepDisplayOn) +
        ',"jiggleMouse":' + (& $flag $JiggleMouse) +
        ',"intervalSeconds":30,"diagnosticLogging":false,"startupInitialized":true' +
        ',"firstRunCompleted":true}'

    Set-Content -Path (Join-Path $dataDir 'settings.json') -Value $document -NoNewline
}

function Stop-App($process) {
    if ($null -eq $process) { return }

    Start-Process $exe -ArgumentList '--shutdown-for-update' -Wait -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $process.Refresh()
    if (-not $process.HasExited) { $process.Kill(); Start-Sleep -Seconds 1 }
}

$stash = $null
if (Test-Path $dataDir) {
    $stash = "$dataDir.measure-stash"
    if (Test-Path $stash) { Remove-Item $stash -Recurse -Force }
    Move-Item $dataDir $stash
}

try {
    # -----------------------------------------------------------------------
    Step 'Warm launch: how long until the engine owns the session'
    # -----------------------------------------------------------------------
    Write-Settings -Stopped $true -KeepDisplayOn $true -JiggleMouse $false

    # A cold first launch measures the disk cache, not the app, so it is warmed
    # and discarded before the timed one.
    $warm = Start-Process $exe -ArgumentList '--startup' -PassThru
    for ($i = 0; $i -lt 100 -and -not (Test-EngineOwned); $i++) { Start-Sleep -Milliseconds 50 }
    Stop-App $warm
    Start-Sleep -Seconds 1

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $app = Start-Process $exe -ArgumentList '--startup' -PassThru

    while (-not (Test-EngineOwned) -and $watch.Elapsed.TotalSeconds -lt 10) {
        Start-Sleep -Milliseconds 20
    }

    $watch.Stop()
    $launchSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
    Record 'PERF-1' 'warm launch to owning the session, 2s gate' "$launchSeconds s" ($launchSeconds -le 2.0)

    # -----------------------------------------------------------------------
    Step 'Stopped app holds nothing'
    # -----------------------------------------------------------------------
    Start-Sleep -Seconds 3
    Record 'A02a' 'a stopped app holds no wake request' $(if (Test-WakeRequestHeld) { 'held' } else { 'none' }) (-not (Test-WakeRequestHeld))

    # -----------------------------------------------------------------------
    Step 'A17: the requests match the controls'
    # -----------------------------------------------------------------------
    Write-Settings -Stopped $false -KeepDisplayOn $true -JiggleMouse $false
    Start-Sleep -Seconds 6
    Record 'A17a' 'running with keep display on holds a request' $(if (Test-WakeRequestHeld) { 'held' } else { 'none' }) (Test-WakeRequestHeld)

    Write-Settings -Stopped $false -KeepDisplayOn $false -JiggleMouse $false
    Start-Sleep -Seconds 6
    Record 'A17b' 'running with both switches off still keeps the system awake' $(if (Test-WakeRequestHeld) { 'held' } else { 'none' }) (Test-WakeRequestHeld)

    Write-Settings -Stopped $true -KeepDisplayOn $true -JiggleMouse $false
    Start-Sleep -Seconds 6
    Record 'A04a' 'a Stop written elsewhere releases the request' $(if (Test-WakeRequestHeld) { 'held' } else { 'none' }) (-not (Test-WakeRequestHeld))

    # -----------------------------------------------------------------------
    Step 'A19: a second launch never starts a second engine'
    # -----------------------------------------------------------------------
    $before = @(Get-Process -Name 'MouseJiggler' -ErrorAction SilentlyContinue).Count
    $second = Start-Process $exe -ArgumentList '--startup' -PassThru
    Start-Sleep -Seconds 3
    $after = @(Get-Process -Name 'MouseJiggler' -ErrorAction SilentlyContinue).Count
    Record 'A19' 'a duplicate quiet launch leaves one engine' "$before before, $after after" ($after -eq $before)

    # -----------------------------------------------------------------------
    Step 'Memory and handles with Settings closed'
    # -----------------------------------------------------------------------
    Write-Settings -Stopped $false -KeepDisplayOn $true -JiggleMouse $false
    Start-Sleep -Seconds 6
    $app.Refresh()

    $privateMiB = [math]::Round($app.PrivateMemorySize64 / 1MB, 1)
    Record 'PERF-3' 'private bytes with Settings closed, 80 MiB gate' "$privateMiB MiB" ($privateMiB -le 80)

    $handlesBefore = $app.HandleCount

    # -----------------------------------------------------------------------
    Step "CPU over $CpuMinutes minute(s) at default settings"
    # -----------------------------------------------------------------------
    $app.Refresh()
    $cpuStart = $app.TotalProcessorTime
    $wallStart = Get-Date

    Start-Sleep -Seconds ($CpuMinutes * 60)

    $app.Refresh()
    $cpuUsed = ($app.TotalProcessorTime - $cpuStart).TotalSeconds
    $wallUsed = ((Get-Date) - $wallStart).TotalSeconds
    $percent = [math]::Round(100.0 * $cpuUsed / $wallUsed, 3)

    Record 'PERF-2' "average CPU of one logical core over $CpuMinutes min, 0.5% gate" "$percent%" ($percent -le 0.5)

    $app.Refresh()
    $privateAfter = [math]::Round($app.PrivateMemorySize64 / 1MB, 1)
    $growth = [math]::Round($privateAfter - $privateMiB, 1)
    Record 'PERF-3b' 'private bytes growth during the run, 10 MiB gate' "$growth MiB" ([math]::Abs($growth) -le 10)

    $handleGrowth = $app.HandleCount - $handlesBefore
    Record 'PERF-3c' 'handle growth while idle' "$handleGrowth handles" ($handleGrowth -le 50)

    # -----------------------------------------------------------------------
    Step 'Abrupt termination leaves nothing behind'
    # -----------------------------------------------------------------------
    Record 'A15a' 'a wake request is held before the kill' $(if (Test-WakeRequestHeld) { 'held' } else { 'none' }) (Test-WakeRequestHeld)

    $app.Kill()
    Start-Sleep -Seconds 3

    # Windows releases a thread execution state when the thread dies, so nothing
    # should survive a kill. This is the check that would catch a power-plan
    # change, which the app must never make.
    Record 'PERF-6' 'no wake request survives an abrupt kill' $(if (Test-WakeRequestHeld) { 'held' } else { 'none' }) (-not (Test-WakeRequestHeld))
    Record 'PERF-6b' 'no engine still owns the session after the kill' $(if (Test-EngineOwned) { 'owned' } else { 'free' }) (-not (Test-EngineOwned))

    $app = $null
}
finally {
    Get-Process -Name 'MouseJiggler' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }

    Start-Sleep -Seconds 1

    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($null -ne $stash -and (Test-Path $stash)) { Move-Item $stash $dataDir }
}

Write-Host ''
Write-Host 'Environment' -ForegroundColor Cyan
Write-Host ("  OS:        " + (Get-CimInstance Win32_OperatingSystem).Caption + " build " + [System.Environment]::OSVersion.Version.Build)
Write-Host ("  CPU:       " + (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name).Trim())
Write-Host ("  Logical:   " + [System.Environment]::ProcessorCount)
Write-Host ("  Commit:    " + (git -C $root rev-parse --short HEAD))

$failed = @($results | Where-Object { -not $_.Passed })

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) gate(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'Every measured gate passed.' -ForegroundColor Green
