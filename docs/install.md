# Installing

## Supported Windows

| Windows | Architecture | Supported |
| --- | --- | --- |
| Windows 10 22H2, build 19045 | x64 | Yes |
| Windows 11 build 22000 or later | x64 | Yes |
| Windows 10 or 11 | ARM64, including x64 emulation | No |
| Windows 10, 32-bit | x86 | No |
| Windows Server, any version | Any | No |
| Windows in S mode | Any | No |

The excluded rows are not tested, and the installer refuses to run on them rather than
installing something nobody has verified.

.NET Framework 4.8 is required and is already part of every supported Windows version, so
there is nothing extra to download. If setup reports it as missing, something is wrong with
the Windows installation itself. Repair it through Microsoft's runtime download rather than
working around the check.

## Installer or portable

The installer is per-user. It never asks for administrator rights, installs to
`%LOCALAPPDATA%\Programs\MouseJiggler`, adds a Start menu shortcut, and offers to start the
app when you sign in. That option is checked by default and you can change it later.

The portable ZIP contains the same application. Extract it anywhere and run
`MouseJiggler.exe`. It does not register itself to start with Windows unless you switch that
on in Settings.

Both editions read and write the same settings at `%LOCALAPPDATA%\MouseJiggler`, so moving
from one to the other keeps your preferences and your explicit Stop.

## Releases are unsigned

Release artifacts are not code-signed. This is a deliberate decision for version 1.0 rather
than an oversight: signing would mean either a paid certificate or a dependency on a signing
service, and neither belongs in a small open-source utility at its first release.

The practical consequence is that Windows SmartScreen will most likely warn you the first
time you run the installer. Verify the download against the published SHA-256 checksum
before running it:

    Get-FileHash .\MouseJiggler-1.0.0-windows-x64-setup.exe -Algorithm SHA256

Compare that against `SHA256SUMS.txt` on the release page. Do not disable SmartScreen, your
antivirus, or any other protection to make this app run.

## Upgrading

Run the newer installer. It keeps the same install location, your settings, your explicit
Stop, and whatever you or Windows decided about starting at sign-in. It asks the running copy
to close first, and if a copy is running from a different folder it says so and stops rather
than closing the wrong one.

Downgrading is refused, because an older build may not understand a newer settings file.

## Uninstalling

Uninstall through Settings, Apps, Installed apps. It removes the installed files, the Start
menu shortcut and its own startup entry.

Your settings are kept by default. The uninstaller offers to delete them, unchecked, and the
prompt notes that a portable copy shares the same data.

Close any portable copy before you say yes, in this session and in any other signed-in session.
A copy that is still running holds the settings in memory and writes them back on its next
save, so deleting them underneath it would not last. The prompt says this too, because it is
the part the uninstaller cannot do for you: the app holds its settings lock only while it is
actually writing, so an idle copy is not detectable from here.

What the uninstaller does check is that no save is in flight. If one is, it leaves the settings
alone and tells you, rather than deleting a file that is being rewritten as it goes.

Deleting removes the settings file, its backup, the lock file, any quarantined copies from a
recovery, and the app's own log files. A file you put in that folder yourself is left where you
put it, and the folder itself is only removed if nothing else is in it.

## Updates

There is no background updater, no update checker and no service. Choosing View releases in
the app opens the releases page in your browser, and only when you click it.
