# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Working towards 1.0.0. Nothing has been released yet. The acceptance matrix in
[issue #14](https://github.com/craigtrim/mouse-jiggler/issues/14) is partly executed: the
performance gates and the functional rows that need no absent hardware and no unattended input
injection have run on one machine, and everything else is **not run**. No build here should be
treated as tested on Windows 11, or on a laptop, or with a screen reader. What has and has not
been executed is listed row by row in
[docs/acceptance/v1.0.0.md](docs/acceptance/v1.0.0.md).

### Changed

- `Alt+A` in the Settings window now applies settings. It previously moved focus to the
  inactivity interval field, which has moved to `Alt+T`. Apply is a command and `Alt+A` is
  where a Windows user reaches for it, and the alternative was a shortcut that collided
  whenever the About section was open.
- The status button in the Settings window answers to `Alt+S` in both of its states. It was
  `Alt+S` while it read Start and `Alt+T` while it read Stop, and `Alt+T` now belongs to the
  interval field. One button that is never both things at once needs only one shortcut.

### Added

- Solution skeleton, shared contracts and MIT licensing.
- Versioned settings with atomic persistence and a Stop that survives restarts.
- The activity state machine and a daylight-saving-correct daily scheduler.
- Automatic power and session detection, and the Windows keep-awake request.
- Idle-only pointer movement with multi-monitor coordinates.
- The tray application, the Settings window, and single-instance IPC.
- An Apply button in the Settings window, which commits exactly what Save commits and
  leaves the window open, so the status line and the schedule preview can be seen
  reacting without closing and reopening.
- Per-user startup registration through the HKCU Run key.
- Bounded local diagnostics, off by default.
- The per-user installer, the portable ZIP and packaging verification.
- Windows CI and the release workflow.
- An application icon at 16, 20, 24, 32, 48 and 256 pixels, generated from the artwork drawn in
  code so the file cannot drift from the tray.
- Build, test and release scripts that CI runs, so a green run on a desk and a green run on an
  agent mean the same thing.

### Fixed

Everything here was found before any release, so none of it ever reached a user. It is recorded
because each one is a mistake worth not repeating.

- Three of the four single-instance listeners could die the moment the app started. The pipe
  security descriptor granted the current user read and write but not `CreateNewInstance`, so
  every listener after the first was refused by the descriptor the process had just applied to
  itself. Whether it happened at all depended on which listener won the race to create the pipe,
  which is why it surfaced as an intermittently failing test rather than as a bug report. A
  listener now rebuilds itself after an unexpected fault rather than leaving the pool a
  listener short in silence.
- A truncated settings file started the app. No field was required, so a missing `stopped`
  became `false` and a damaged document parsed cleanly into a running app.
- A damaged settings file was silently replaced by defaults, and the resulting Error status was
  unreachable, so the user saw a healthy stopped app over their damaged settings.
- Upgrade detection had never once been true. `SetupSetting('AppId')` returns the escaped form
  of a GUID, so the uninstall key it named did not exist. Every upgrade reset the startup
  decision, never asked the running copy to close, and skipped the downgrade guard.
- A Start that could not be saved left the tray icon stopped and said nothing, which is
  indistinguishable from a click that never registered.
- The Settings window cut off its own text twice over: the root panel could never exceed the
  client area, so `AutoScroll` had nothing to scroll and the buttons were unreachable, and a
  wrapping label measured at one width and laid out at another lost its last line behind the
  next group box.
- Four Alt-key mnemonics were claimed twice, so Alt cycled between two controls instead of
  acting.
- `Dispose` threw and abandoned its own cleanup when the pipe handle had already closed, leaving
  the single-instance mutex held so the next launch would refuse to start.
- The keep-awake retry ignored its backoff on the release path, making it a busy loop wearing
  the costume of a retry policy.
- `firstRunCompleted` could never become true, so Settings reopened on every launch.
- Exit bypassed the coordinator, so `--shutdown-for-update` always reported success even with an
  unsaved Stop.
- The five-second power fallback never ran, so a missed notification left a stale reading
  forever.
- The startup registry entry survived uninstall, pointing at a folder that no longer existed.
- A silent uninstall blocked forever on a prompt.
- A wrapping label whose text changed after the window was built could be left in a container
  one line too short, so the last line of a sentence was drawn under a group box border. It
  affected about one window in twenty.
- The Settings window never noticed a save made by another instance, so a draft could be
  written on top of newer settings with no warning.
- Save stayed enabled for a schedule the store would reject.
- The release workflow contained a `git fetch --depth=0`, which git rejects outright.
- An installer could ask the wrong copy of the application to close, or configure startup for
  it, because the decision was made on the executable path that the answering process put in
  its own reply. The pipe name is predictable and is a separate kernel object from the
  ownership mutex, so a same-user process can hold it before the application starts and claim
  to be anything. The kernel is now asked which process is really on the other end.
- That check was then asked at the wrong moment, and refused every legitimate caller. The owner
  closes its pipe instance as soon as it has answered, so asking after reading the reply returns
  `ERROR_PIPE_NOT_CONNECTED`, and a question about timing was reported as one about identity.
  Every unit test passed throughout, because an in-process server lingers long enough.
- Deleting settings during uninstall left the quarantined `settings.invalid-*` copies behind.
  They are whole settings files, so answering yes to "delete my settings and diagnostics" left
  the settings on disk. It also removed the `Logs` tree wholesale rather than the log files it
  owns, and never checked whether a portable copy of the same user was still using the
  directory, in which case the deletion would not even take effect.
- No workflow installed the installer compiler, so a release build produced no installer at
  all. `package.ps1` warned and carried on, and the failure surfaced several steps later as a
  complaint about how many files were found to upload. The recorded hash and publisher
  signature in `tool-versions.json` were never checked by anything either.
- The count of suppressed repeat diagnostics was tracked and then discarded, so one failure a
  minute and sixty failures a minute produced identical output.
- The Settings window opened at 533 logical pixels wide rather than the 540 specified, because
  the size was derived entirely from content with no floor.
- The tray icon was a plain grey square when stopped. It said nothing about which application
  it belonged to, which is most of what a notification area icon is for. Every state now draws
  the same mouse and varies what the mouse is doing.
- The Settings window showed the stock .NET icon in its title bar, in Alt+Tab and on the
  taskbar. `<ApplicationIcon>` puts an icon on the executable for the shell and nothing else,
  so a form that never assigns `Icon` gets the framework default.
