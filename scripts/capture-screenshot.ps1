<#
.SYNOPSIS
    Captures the Settings window in each state the acceptance record needs.

.NOTES
    Run under Windows PowerShell 5.1 (powershell.exe), not pwsh. PowerShell 7
    resolves System.Drawing to System.Drawing.Common, which Add-Type cannot
    reference here without extra packaging.

.DESCRIPTION
    Uses PrintWindow with PW_RENDERFULLCONTENT, which asks the target window to
    render itself into a bitmap the script owns.

    It must never use Graphics.CopyFromScreen or any other screen grab. A screen
    grab captures whatever pixels happen to be on the display, so a window that
    fails to come to the front means the capture silently contains someone's
    terminal, editor or mail instead. PrintWindow cannot do that: it renders the
    one window handle it is given and nothing else, whether or not that window is
    visible, focused, or covered by something.

    Every state is produced by writing a real settings document and launching the
    real application against a scratch profile, so no image here is a mock-up:
    each one is the window a user would actually see, and never a real
    configuration.

    The one state this cannot produce is battery-paused. It needs a machine with
    a battery, so it stays outstanding in the acceptance record rather than being
    staged with a fake power reading.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [ValidateSet('stopped', 'manual', 'scheduled', 'waiting', 'error', 'all')]
    [string] $State = 'all'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src/MouseJiggler.App/bin/$Configuration/net48/MouseJiggler.exe"

if (-not (Test-Path $exe)) {
    throw "Build the app first: dotnet build -c $Configuration. Expected $exe"
}

Add-Type -AssemblyName System.Drawing
Add-Type -Language CSharp @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class WindowCapture
{
    // Render the full window content, including parts never painted to the screen.
    private const int PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Makes this process per-monitor aware before it asks anything about the
    /// target window. Without it Windows virtualizes the answers: GetWindowRect
    /// hands back coordinates divided by the scale factor, so the bitmap comes
    /// out smaller than the window and PrintWindow renders a cropped corner of a
    /// form that is perfectly fine.
    /// </summary>
    public static void MakeDpiAware()
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        SetProcessDpiAwarenessContext(new IntPtr(-4));
    }

    public static Bitmap Capture(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            throw new ArgumentException("A window handle is required.", "window");
        }

        RECT bounds;
        if (!GetWindowRect(window, out bounds))
        {
            throw new InvalidOperationException("The window has no bounds.");
        }

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The window has no size.");
        }

        Bitmap bitmap = new Bitmap(width, height);

        try
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try
                {
                    // The one call that touches pixels, and it addresses a single
                    // window handle. Nothing else on the display can reach this bitmap.
                    if (!PrintWindow(window, dc, PW_RENDERFULLCONTENT))
                    {
                        throw new InvalidOperationException("PrintWindow refused the window.");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(dc);
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
'@ -ReferencedAssemblies System.Drawing, System.Windows.Forms

[WindowCapture]::MakeDpiAware()

$imagesDirectory = Join-Path $root 'docs/images'
if (-not (Test-Path $imagesDirectory)) {
    New-Item -ItemType Directory -Path $imagesDirectory | Out-Null
}

function New-SettingsDocument {
    param(
        [bool] $Stopped,
        [string] $RunMode,
        [bool] $ScheduleEnabled,
        [string] $Start,
        [string] $End
    )

    $flag = { param($value) if ($value) { 'true' } else { 'false' } }

    return '{"schemaVersion":1,"revision":2' +
           ',"stopped":' + (& $flag $Stopped) +
           ',"runMode":"' + $RunMode + '"' +
           ',"scheduleEnabled":' + (& $flag $ScheduleEnabled) +
           ',"scheduleStart":"' + $Start + '","scheduleEnd":"' + $End + '"' +
           ',"dayMask":127,"pauseOnBattery":false,"keepDisplayOn":true,"jiggleMouse":true' +
           ',"intervalSeconds":30,"diagnosticLogging":false,"startupInitialized":true' +
           ',"firstRunCompleted":true}'
}

# A window that certainly contains this moment, and one that certainly does not,
# whatever time of day the capture happens to run at.
$now = Get-Date
$insideStart = $now.AddHours(-2).ToString('HH:mm')
$insideEnd = $now.AddHours(2).ToString('HH:mm')
$outsideStart = $now.AddHours(3).ToString('HH:mm')
$outsideEnd = $now.AddHours(5).ToString('HH:mm')

$states = @(
    @{ Name = 'stopped';   File = 'settings.png';                Document = $null }
    @{ Name = 'manual';    File = 'settings-running-manual.png'; Document = (New-SettingsDocument $false 'Manual' $false '08:00' '17:00') }
    @{ Name = 'scheduled'; File = 'settings-scheduled.png';      Document = (New-SettingsDocument $false 'Scheduled' $true $insideStart $insideEnd) }
    @{ Name = 'waiting';   File = 'settings-waiting.png';        Document = (New-SettingsDocument $false 'Scheduled' $true $outsideStart $outsideEnd) }
    @{ Name = 'error';     File = 'settings-error.png';          Document = '{ this document is damaged' }
)

if ($State -ne 'all') {
    $states = @($states | Where-Object { $_.Name -eq $State })
}

$real = Join-Path $env:LOCALAPPDATA 'MouseJiggler'
$stashed = $null

if (Test-Path $real) {
    $stashed = "$real.screenshot-stash"
    if (Test-Path $stashed) { Remove-Item $stashed -Recurse -Force }
    Move-Item $real $stashed
}

try {
    foreach ($capture in $states) {
        $app = $null

        try {
            if (Test-Path $real) { Remove-Item $real -Recurse -Force }
            New-Item -ItemType Directory -Path $real -Force | Out-Null

            if ($null -ne $capture.Document) {
                Set-Content -Path (Join-Path $real 'settings.json') -Value $capture.Document -NoNewline
            }

            # With no document this is a genuine first run and Settings opens by
            # itself. Every other state has firstRunCompleted set, so the window is
            # asked for by a second launch talking to the first over the pipe.
            $app = Start-Process $exe -ArgumentList '--startup' -PassThru
            Start-Sleep -Seconds 3

            $handle = [IntPtr]::Zero
            for ($i = 0; $i -lt 40; $i++) {
                $app.Refresh()
                if ($app.HasExited) { throw ("The app exited before its window appeared: " + $capture.Name) }
                if ($app.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $app.MainWindowHandle; break }
                Start-Sleep -Milliseconds 250
            }

            if ($handle -eq [IntPtr]::Zero) {
                Start-Process $exe -Wait

                for ($i = 0; $i -lt 40; $i++) {
                    Start-Sleep -Milliseconds 250
                    $app.Refresh()
                    if ($app.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $app.MainWindowHandle; break }
                }
            }

            if ($handle -eq [IntPtr]::Zero) {
                throw ("The Settings window never appeared: " + $capture.Name)
            }

            # Let the layout settle so the capture is not of a half drawn form.
            Start-Sleep -Seconds 2

            $target = Join-Path $imagesDirectory $capture.File
            $bitmap = [WindowCapture]::Capture($handle)

            try {
                $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
                Write-Output ("{0,-10} -> {1} ({2}x{3})" -f $capture.Name, $capture.File, $bitmap.Width, $bitmap.Height)
            }
            finally {
                $bitmap.Dispose()
            }
        }
        finally {
            if ($null -ne $app) {
                Start-Process $exe -ArgumentList '--shutdown-for-update' -Wait -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 1
                $app.Refresh()
                if (-not $app.HasExited) { $app.Kill() }
                Start-Sleep -Milliseconds 500
            }
        }
    }
}
finally {
    if (Test-Path $real) { Remove-Item $real -Recurse -Force }
    if ($null -ne $stashed -and (Test-Path $stashed)) { Move-Item $stashed $real }
}
