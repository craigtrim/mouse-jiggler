# Troubleshooting

## The computer still slept, or the screen still locked

Keeping the computer awake and preventing an idle lock are different things. Mouse Jiggler
asks Windows to stay awake, and that request does not by itself stop a screensaver or an
idle lock.

If you need the session to stay unlocked, leave **Jiggle mouse** switched on. With it off,
the app is only asking the machine to stay powered, and Windows may still lock the session.

Neither control is a guarantee. A managed machine can have a policy that locks regardless,
and no application setting overrides that.

## The status says "Paused, on battery"

**Pause on battery** is on by default, and it stops the app whenever Windows reports
anything other than external power. Plug the machine in, or clear that checkbox in Settings.

The app reads power from Windows and there is deliberately no manual override. A setting
that let you assert you were plugged in when you were not would only flatten the battery.

## The status says "Power status unavailable"

Windows returned an unknown power status. While **Pause on battery** is on, unknown is
treated as not plugged in, because acting on an unknown reading is how a laptop ends up flat
in a bag. Clearing that checkbox lets the app run whatever power reports.

## It says pointer movement failed

Windows refused the injected input, usually because another program is blocking it, often one
running with higher privileges than Mouse Jiggler.

Choose **Retry**. The app does not retry this on its own indefinitely, and it will not ask
for administrator rights to work around it. Keeping the computer awake generally carries on
working even when pointer movement does not, and the status says exactly that instead of
claiming everything is fine.

## It did not start when I signed in

Windows has the final say over startup apps, and it is normal for it to disable one. Check
Settings, Apps, Startup, or the Startup tab in Task Manager, and switch Mouse Jiggler back on
there.

The checkbox in the app only manages its own registry entry. It cannot, and deliberately does
not try to, overrule what you or Windows decided in Startup settings.

If you moved a portable copy after switching startup on, Windows is still pointing at the old
folder. Switch it off, then on again, from the new location.

## My settings were not saved

The app tells you when a save fails and retries after one, five and thirty seconds. Stop
always takes effect immediately in memory even when it cannot be written, but if it never
gets written then the previous saved state returns after a restart. That is why the warning
stays visible until the save succeeds.

A settings file that is damaged or from a newer version leaves the app stopped and preserves
the original file. Use **Reset settings** to start again from defaults; the old file is kept
alongside as a timestamped copy.

## Collecting diagnostics

Open **Settings**, expand **About and diagnostics**, and choose **Copy diagnostics**. That puts
a summary on the clipboard. If the clipboard is busy the app says so and you can try again.

Read it before you paste it anywhere. It is short by design and the whole of it is listed
below, so there is nothing in it you have not been shown.

### Exactly what Copy diagnostics contains

| Field | Example | Where it comes from |
| --- | --- | --- |
| App version | `1.0.0.0` | The assembly version |
| Edition | `installed` or `portable` | Whether the marker file sits beside the executable |
| Windows | `10.0 build 19045` | `Environment.OSVersion` |
| Architecture | `x64` | Pointer size |
| Framework release | `528449` | The .NET Framework release number from the registry |
| Data directory | `%LOCALAPPDATA%\MouseJiggler` | A literal token, never expanded |
| Status | `RunningManual` | The computed status |
| Power | `External`, `Battery` or `Unknown` | As Windows reports it |
| Session | `ActiveUnlocked`, `Locked`, `Disconnected` or `Unknown` | As Windows reports it |
| System awake requested | `True` or `False` | What was asked for |
| Display awake requested | `True` or `False` | What was asked for |
| Pointer movement allowed | `True` or `False` | What the policy decided |
| Wake request failed | `True` or `False` | Whether Windows refused it |
| Input request failed | `True` or `False` | Whether injection failed |
| Settings | Stopped, mode, schedule on or off, window, day mask, pause on battery, keep display on, jiggle mouse, interval, diagnostic logging | Your saved preferences |
| Recent events | `2026-09-09 10:31:02  WakeAcquire  wake.applyFailed  win32=5` | The last 100 sanitized records |

The data directory is the only path that appears, and it appears as the literal token
`%LOCALAPPDATA%` rather than the expanded form, because the expanded form contains your
account name.

### What it never contains

No keystrokes. No pointer positions or movement history. No window titles or process names.
No account name, SID or computer name. No expanded paths. No settings file contents beyond
the named preferences above. No exception text and no stack traces, because a free-text field
is how user data ends up in a log.

### The log file, if you turn it on

**Enable diagnostic logging** is off by default. When you switch it on, the same records are
written to `%LOCALAPPDATA%\MouseJiggler\Logs\mousejiggler.log` as JSON Lines, one object per
line:

    {"utc":"2026-09-09T10:31:02.417Z","version":"1.0.0.0","subsystem":"WakeAcquire","code":"wake.applyFailed","nativeError":5}

| Field | Always present | Meaning |
| --- | --- | --- |
| `utc` | Yes | When it happened, in UTC to the millisecond |
| `version` | Yes | The app version that wrote the line |
| `subsystem` | Yes | `ConfigRead`, `ConfigSave`, `PowerQuery`, `SessionQuery`, `WakeAcquire`, `WakeRelease`, `InputRead`, `InputSend`, `StartupRegistration` or `ShellOrIpc` |
| `code` | Yes | A stable short identifier such as `wake.applyFailed` |
| `nativeError` | No | The Windows error number, when there was one |
| `suppressedRepeats` | No | How many identical records this line stands in for |

The current file is capped at 256 KiB with four numbered backups, so the logs never exceed
about 1.25 MB in total. Repeats of the same code are written at most once a minute, so a fault
that persists for an hour does not fill the file.

The line that ends each of those minutes carries `suppressedRepeats`, which is how many
identical records were dropped behind it. That number is worth reading: one failure a minute
and sixty failures a minute produce the same single line, and only this tells them apart. A
line without the field is not standing in for anything. The same count appears in **Copy
diagnostics** as `(+58 more suppressed)`.

There is no per-jiggle logging. A successful jiggle is not an event.

**Open log folder** opens that directory and nothing else, and creates it only when you ask.

### Nothing is sent anywhere

There is no upload, no update check, no telemetry identifier and no analytics. The app makes
no network connections at all, and that is enforced by a test that fails if any shipped
assembly so much as names a networking type.
