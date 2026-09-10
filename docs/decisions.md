# Decision record

Every decision in the [1.0 epic](https://github.com/craigtrim/mouse-jiggler/issues/2), and where it is implemented. The epic text is normative; this file maps it onto the code.

| ID | Decision | Where it lives |
| --- | --- | --- |
| D01 | .NET Framework 4.8, C# 12, x64, SDK-style projects, MIT licence, no runtime NuGet packages | `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `LICENSE` |
| D02 | Core / Windows / App / Tests split, one owner thread, pure evaluator, generation numbers | `docs/architecture.md`, `Core/Abstractions`, `Core/Commands/AppCommand.cs` |
| D03 | First launch shows Settings and starts stopped; defaults; settings file location | `Core/Settings/SettingsV1.cs`, `Windows/Settings` |
| D04 | Start, Start now, Start on schedule, Stop, Exit, and the priority order | `Core/Commands`, `Core/Activity` |
| D05 | One daily window, inclusive start and exclusive end, DST correctness | `Core/Scheduling` |
| D06 | Automatic power and session detection, lock and suspend behaviour | `Windows/Power`, `Windows/Sessions` |
| D07 | Keep-awake requests and the one-pixel pointer batch | `Windows/Input`, `Windows/Power/ExecutionStateController.cs` |
| D08 | Computed status, latched faults, bounded retries | `Core/Activity`, `Core/Abstractions/OperationResult.cs` |
| D09 | Native tray and Settings conventions, HKCU Run registration | `App`, `Windows/Startup` |
| D10 | Per-user installer, portable ZIP, opt-in diagnostics, user-initiated updates | `installer/`, `scripts/`, `Windows/Diagnostics` |
| D11 | The disposition of every optional note from the requirements discussion | The epic table; nothing here implements an excluded item |

## Decisions taken while implementing

These resolve points the epic left open. Each one is written back into the owning issue.

### DPI awareness is configured in App.config only

.NET Framework 4.7 and later read `DpiAwareness` from the `System.Windows.Forms.ApplicationConfigurationSection` in `App.config`. A `dpiAware` or `dpiAwareness` entry in the application manifest takes precedence and disables that mechanism, so declaring both would silently defeat the PerMonitorV2 setting. The manifest therefore carries the execution level, the Windows 10 compatibility GUID and `longPathAware`, and no DPI entry at all.

### Nullable annotations against an unannotated framework

.NET Framework 4.8 reference assemblies carry no nullable annotations, so framework APIs are nullable-oblivious rather than nullable-hostile. Enabling `Nullable` with warnings as errors is therefore practical, but it leaves blind spots where the compiler cannot know that a framework method returns null.

`NotNullWhenAttribute` is hand-declared in `Core/Compatibility/NullableAttributes.cs`. One contract needs it. `JiggleEligibility.TryChooseTarget` sets its `out JiggleTarget?` only when it returns true, and without the attribute the caller in `MouseJigglerDevice` had to write `|| target == null` after already testing the return value. That second test could never fire, so it read as a real possibility that the method might return true with nothing in the parameter, and anyone maintaining it had to work out that it could not.

It is `internal`, and only `Core` declares it. Roslyn reads these attributes out of metadata by name, so `Windows` gets the flow analysis from `Core` without being able to name the type, which is what makes one copy enough. A second copy in another project would collide inside the test project, which already sees the internals of two assemblies, and that collision is a warning this build treats as an error.

`MemberNotNullAttribute` is not declared. It describes a field a constructor leaves unassigned for a helper to fill in, and nothing here is built that way: there is not one null-forgiving initializer in `src`. Declaring an attribute nothing applies would be an unused type the compiler cannot warn about. If a contract later needs it, this is the file. `IsExternalInit` is deliberately absent, since the contracts use ordinary immutable classes rather than records.

### UseWindowsForms on net48

`UseWindowsForms=true` is honoured for `net48` under SDK 8.0.131 and pulls in the Windows Forms and drawing references. Removing it fails the build with `CS0234`, so no explicit `Reference` items are needed.

### The x64 test host must be named explicitly

`dotnet test` does not auto-discover a `.runsettings` file at the repository root. `RunSettingsFilePath` in `Directory.Build.props` points at it, which is what puts the test host in x64 and targets `.NETFramework,Version=v4.8`.

### Wrapping labels reserve their tallest message in advance

The Settings window has several labels whose text changes while it is open: the status line, the
qualifying detail, the schedule preview, and three warnings. All of them wrap.

A label that is given new text grows, and its container is not reliably asked to grow with it.
The layout settles, and stays settled, with a group box built for two lines around a sentence
that needs three, so the last line is drawn under the border or behind the group below. It
happened for roughly one window in twenty, on whichever label lost the race, which made it read
as flakiness in the tests rather than as a window that was sometimes wrong.

Several fixes were tried against the containers: laying out from the leaves upward, reassigning
the same text, cycling `AutoSize`, pinning each label's wrap width to the width it was given,
and repairing container heights after the fact. Every one of them worked most of the time. That
is the worst available outcome, because it converts a layout bug into an intermittent one.

What works is not arguing with the containers at all. Each of these labels draws its text from a
small and known set, so the tallest member of that set is measured with `TextRenderer` and
reserved as the label's minimum height before anything is laid out. The container is then built
once, at a size no later message can exceed, and nothing ever needs to grow.

Three details are worth keeping.

`TextRenderer.MeasureText` must be given an unbounded proposed height: passing zero constrains
the measurement to zero and it answers with a single line however much text there is, which is
the same mistake in a different place.

The wrap width is a constant rather than the width a label happens to have, because a label's
width is an output of wrapping, so feeding it back in makes the measurement and the layout chase
each other. It is applied when the label is created, before it is added to anything: applied
afterwards, it changes the label's height inside a container that has already decided how tall
it is, and the container does not reliably reconsider.

The same reasoning applies to the one other thing in the window that wraps. Seven day
checkboxes do not fit on one row, so their panel wraps, and how many rows it takes depends on
how wide it is. It carries the same fixed maximum width for the same reason: measured at one
width and laid out at another, it was sometimes measured as one row and drawn as two, and the
group box was built a row short.

### The pipe answer is checked against the process that gave it

Two IPC commands act on a running copy of the application: `SHUTDOWN_FOR_UPDATE` closes it, and `INSTALL_INITIALIZE` writes a startup entry for it. Both are refused unless the copy that answers is running the same executable as the caller, so that an installer for one copy cannot close or reconfigure another.

The owner reports its own process id and executable path in reply to `IDENTIFY`. Neither is believed on its own. `GetNamedPipeServerProcessId` is asked, on the client's own handle while it is still connected, which process is on the other end; that process is resolved to its image path; and the reply has to agree with both. The path that ownership is then decided on is the one the operating system gave, never the one in the reply.

The pipe is already restricted to a single user, so this is not the only thing between the application and a hostile caller. It closes something narrower and quite reachable. The pipe name is derived from the SID and the session id, so it is predictable, and it is a separate kernel object from the ownership mutex, so holding the name does not require owning the engine. A same-user process can take that name before this application ever starts. Without the check it could then name any path it liked and an installer would act on the claim.

An identity that cannot be verified is refused rather than assumed. Refusing costs the installer one message asking the user to close the other copy. Proceeding could close something else.

### Reproducible builds

`Deterministic`, `ContinuousIntegrationBuild` and `PathMap` make the managed output content-derived rather than path-derived or time-derived. Two constraints survive that: embedded resource bytes must already be identical, since `PathMap` cannot reach inside them, and `MouseJiggler.exe.config` is copied rather than compiled, so it sits outside the compiler guarantee and is hashed separately.

The guarantee stops at the managed payload. Neither container reproduces byte for byte: the ZIP records file timestamps and the Inno installer embeds a build timestamp, so rebuilding one commit produces different container bytes around identical contents. Issue #13 allows exactly this and asks for the boundary to be recorded rather than papered over, so `verify-release.ps1` compares the assemblies and the executable from two clean builds and says nothing about the containers.

That boundary is why a release rerun refuses rather than comparing and continuing. A rerun cannot show that it would upload the same artifacts, because it provably would not, so the only safe answer is to leave the existing draft alone and say so.
