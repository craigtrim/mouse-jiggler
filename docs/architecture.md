# Architecture

## Project boundaries

Four projects, with dependencies pointing in one direction only.

    MouseJiggler.App  ->  MouseJiggler.Windows  ->  MouseJiggler.Core
            |                                            ^
            +--------------------------------------------+

`Core` holds the rules and the contracts. It contains no P/Invoke, no Windows Forms reference, no registry access, and no call to `DateTime.Now`. Everything it needs to make a decision arrives as an immutable snapshot, which is what makes the decision logic testable with fake clocks and no real hardware.

`Windows` holds the Win32 adapters and file storage. It may call native APIs and touch the file system and registry, but it owns no user interface and never calls a Windows Forms control.

`App` composes the two into one `ApplicationContext`. It owns the hidden top-level native message window, the tray icon, the dispatcher and the coordinator.

`Tests` covers all three with fakes.

## What lives where in App

`App` is the only project that may hold a control, so it is the one most at risk of becoming a
single file that does everything. These are the pieces it is split into, and why each one is
separate rather than folded into the context or the form.

| Type | Holds | Why it is on its own |
| --- | --- | --- |
| `TrayApplicationContext` | Process lifetime, composition, window messages, IPC | The only thing that knows about the process as a whole |
| `TrayMenuController` | The icon and the menu | Decides nothing: renders what it is handed and raises the command that was picked |
| `ActivityCoordinator` | Applying what the evaluator decides, on the owner thread | The one place effects are applied, so there is no lock to forget |
| `RecoveryCoordinator` | The two retry cadences | A backoff that has become a per-tick loop looks identical from outside; the schedule has to be assertable |
| `SettingsForm` | The window and its controls | |
| `AboutPanel` | Version, licence, diagnostics, reset | About the installation rather than about what the app does |
| `SettingsPresenter` | Document to draft, draft to patch, the day mask | No Windows Forms in it, so the fiddly parts can be tested without a window |
| `SettingsValidationPresenter` | Fault code to the words a user reads | The messages matter as much as the validation, and are worth testing |
| `StatusTextFormatter` | Every user-facing sentence about status | One place to check what the app claims |
| `TrayIconFactory`, `IconFileWriter` | The artwork, and packing it into an .ico | The icon is drawn in code, so the file is a build product |

Two contracts in `Core` exist so that `ActivityCoordinator` can be constructed without a real
machine: `IPowerSessionSource`, which `WindowsPowerSessionSource` implements by composing the
power and session adapters, and `IInputDesktopProbe`, which asks whether this thread can reach
the desktop currently receiving input. Both were previously concrete Windows types held
directly, which is why the coordinator had no test of its own.

## One owner thread

A single STA `ApplicationContext` owns the message window, the tray icon and the coordinator. Native callbacks post events to that dispatcher rather than acting directly, and keep-awake acquisition and release both happen on that same thread, because `SetThreadExecutionState` is thread-scoped and a request released from the wrong thread would leak.

File operations run as bounded background work and report completion back as events. The user interface never waits on unbounded I/O.

## The evaluator

One pure function decides everything:

    (SettingsV1, EnvironmentSnapshot) -> DesiredEffects

`DesiredEffects` says whether the system should stay awake, whether the display should stay awake, whether the pointer may move, what the visible status is, and when the next real schedule transition occurs. The evaluator has no side effects. Only the coordinator applies what it returns.

The status is computed here rather than inferred from the state of a checkbox, so a capability that Windows refused is reported as a failure instead of appearing green.

## Generations

Every state or command change increments a generation number before any effect is applied. Queued work carries the generation it was created under, and may act only when that number still matches. This is how a callback scheduled before a Stop, a mode change, a lock, a suspend or disposal is prevented from activating anything afterwards.

Stop clears effects in memory immediately and then persists. It does not wait for the disk write to succeed before cancelling pointer movement and releasing wake requests.

## Priority

The evaluator resolves competing conditions in a fixed order, and the first one that applies becomes the visible status:

1. Exiting, suspended, or unusable configuration.
2. Explicit Stop.
3. Disconnected or unknown session.
4. The AC-only power gate.
5. Schedule membership, in Scheduled mode only.

A locked session is a separate case rather than a rung on that ladder. While locked the app cancels pointer movement and releases the display request, but retains the system-awake request whenever it would otherwise be eligible.
