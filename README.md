# Mouse Jiggler

[![CI](https://github.com/craigtrim/mouse-jiggler/actions/workflows/ci.yml/badge.svg)](https://github.com/craigtrim/mouse-jiggler/actions/workflows/ci.yml)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6.svg)](https://github.com/craigtrim/mouse-jiggler#readme)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![Architecture](https://img.shields.io/badge/arch-x64-lightgrey.svg)](https://github.com/craigtrim/mouse-jiggler#readme)

A small Windows tray utility that keeps the computer awake and generates minimal mouse activity while you are away from the keyboard. It runs manually or on one daily schedule, can pause while on battery, and stays stopped until you explicitly start it again.

Windows 10 22H2 and Windows 11 build 22000 or later, x64 only. Open source under the MIT licence. No accounts, no telemetry, no network traffic.

## Status

Under active development against the [1.0 implementation epic](https://github.com/craigtrim/mouse-jiggler/issues/2). There is no released build yet. The issues in that epic are a plan, not a claim that the software is finished or tested.

The supported versions above are what the code targets and what the installer accepts. They are not all versions it has been run on. Everything executed so far ran on Windows 10 Pro 22H2 build 19045 x64: the automated suite, the packaging and lifecycle checks, the performance gates, and the acceptance rows that need no absent hardware. **Windows 11 has not been run at all**, and neither has a laptop, a second monitor, a screen reader or a second signed-in session. [docs/acceptance/v1.0.0.md](docs/acceptance/v1.0.0.md) lists what was executed and what was not, row by row.

## What it does

The app asks Windows to stay awake with `SetThreadExecutionState`, and optionally moves the pointer one physical pixel and back after a period of inactivity. It sends no clicks and no keystrokes. Both controls are requests to Windows rather than guarantees: with pointer movement switched off, Windows may still lock an idle session.

Start, Stop and the daily schedule live in the tray menu and in one Settings window. Stop persists across schedule boundaries, power changes, unlock, restart and sign-in. Saving a preference cannot start a stopped app.

<img src="docs/images/settings.png" alt="The Mouse Jiggler Settings window, showing the current status, the daily schedule, the behaviour switches, the startup option and the diagnostics controls." width="420">

The window above is captured from the real application by `scripts/capture-screenshot.ps1`, against a scratch settings folder so it shows first-run defaults rather than anyone's configuration. The same script captures the [running, scheduled, waiting and error states](docs/acceptance/v1.0.0.md#settings-window-states) for the acceptance record.

## Building from source

You need Windows and the .NET SDK 8.0.131, which `global.json` pins with `rollForward` disabled. The app itself targets .NET Framework 4.8, which is already present on every supported version of Windows, so building requires the SDK but running the installed app does not.

    dotnet restore MouseJiggler.sln --locked-mode
    dotnet build MouseJiggler.sln -c Release --no-restore
    dotnet test MouseJiggler.sln -c Release --no-build --logger trx

Package versions are pinned centrally in `Directory.Packages.props` and locked in the committed `packages.lock.json` files. Every package is build-time or test-time only. Nothing third-party ships inside the application.

## Layout

| Project | Contains |
| --- | --- |
| `src/MouseJiggler.Core` | Rules and contracts. No Win32, no Windows Forms, no clock or registry access. |
| `src/MouseJiggler.Windows` | Win32 adapters and file storage. |
| `src/MouseJiggler.App` | Windows Forms composition, tray icon and Settings window. |
| `tests/MouseJiggler.Tests` | Unit and integration tests against fakes. |

Further detail is in [docs/architecture.md](docs/architecture.md), the decision record in [docs/decisions.md](docs/decisions.md), and the test policy in [docs/testing.md](docs/testing.md).

## Licence

MIT. See [LICENSE](LICENSE).
