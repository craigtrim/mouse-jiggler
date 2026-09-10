# Contributing

## What you need

Windows, and the .NET SDK 8.0.130 that `global.json` pins with `rollForward` disabled. No
Visual Studio installation is required: the projects are SDK-style and build from the
command line, and the .NET Framework reference assemblies arrive as a NuGet package rather
than a Developer Pack.

## Building

    dotnet restore MouseJiggler.sln --locked-mode
    dotnet build MouseJiggler.sln -c Release --no-restore
    dotnet test MouseJiggler.sln -c Release --no-build --logger trx

`--locked-mode` is deliberate. Package versions are pinned centrally in
`Directory.Packages.props` and locked in the committed `packages.lock.json` files, so a
restore that would change a dependency fails instead of quietly succeeding.

Warnings are errors. Nullable reference types are on. Neither is negotiable in a pull
request, because both exist to keep a class of defect out of a program that runs
unattended for hours.

Building the installer as well needs the pinned Inno Setup compiler:

    ./scripts/install-inno.ps1
    ./scripts/package.ps1

`install-inno.ps1` downloads it from the upstream release named in
`scripts/tool-versions.json` and checks the recorded size, SHA-256 and Authenticode signer
before running any of it. Without a compiler, `package.ps1` still builds the portable ZIP
and says the installer was skipped; CI and the release workflow pass `-RequireInstaller`,
which turns that into a failure.

## Where code belongs

`Core` holds rules and contracts. It has no P/Invoke, no Windows Forms reference, and no
call to `DateTime.Now` or the registry. Everything it needs arrives as an immutable
snapshot. Putting a decision here is what makes it testable with a fake clock instead of a
stopwatch and a real desktop.

`Windows` holds the Win32 adapters and file storage. It may call native APIs; it owns no
user interface.

`App` composes the two. It owns the tray icon, the hidden message window and the one
thread that applies effects.

A change that adds a native call to `Core`, or a form to `Windows`, is in the wrong
project even if it compiles.

## Tests

Ordinary test runs must never move your pointer or touch your real settings. Tests that
have a real effect on the machine carry `[LiveInputFact]` and are skipped unless
`MOUSEJIGGLER_LIVE_INPUT=1` is set on a dedicated desktop. See [docs/testing.md](docs/testing.md).

If you fix a defect, add the test that would have caught it. Several of the bugs found
during initial development were only visible when the real application ran, so a test that
exercises the real path is worth more here than one that exercises a mock.

## Dependencies

The application ships no third-party code, and that is a design constraint rather than an
accident. A pull request that adds a runtime package will be declined. Build-time and
test-time packages are pinned centrally and must be added to
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) in the same change.

## Licence

Contributions are accepted under the MIT licence in [LICENSE](LICENSE).
