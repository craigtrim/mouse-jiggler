# Testing

## Ordinary test runs never touch your mouse

    dotnet test MouseJiggler.sln -c Release --no-build --logger trx

That command runs against fakes only. It moves no pointer, sends no input, reads no real settings file, and writes nothing to the real `Run` registry key. Running the suite while you are working must be safe, so any test that would violate that is gated rather than merely discouraged.

## How the gate works

Tests that inject real input carry `[LiveInputFact]` instead of `[Fact]`. The attribute marks the test skipped unless `MOUSEJIGGLER_LIVE_INPUT` is set to `1` and the process is interactive. Native initialization and injection stay inside the gated test body, including anything that would otherwise sit in a fixture or a static constructor, because a fixture runs during discovery whether or not the test is skipped.

Set the variable only on a dedicated desktop or virtual machine. An interactive session is not by itself a dedicated one, and a stray pointer jump during a real work session is exactly what the gate exists to prevent.

    $env:MOUSEJIGGLER_LIVE_INPUT = '1'
    dotnet test MouseJiggler.sln -c Release --no-build --logger trx
    Remove-Item Env:MOUSEJIGGLER_LIVE_INPUT

## What the fakes cover, and what they cannot

Fake clocks drive every scheduling, timing and retry test, so the suite has no real sleeps and runs in seconds. Fake power, session and native adapters cover the failure paths that are impractical to produce on demand, including a denied registry write, a disk-full settings save and a partial `SendInput` result.

None of that validates physical behaviour. Whether Windows actually stays awake, whether the screensaver is held off, and whether the pointer lands back on its original pixel are questions only a real desktop can answer. Those belong to the acceptance matrix in [issue #14](https://github.com/craigtrim/mouse-jiggler/issues/14) and are recorded there against a named OS build. Continuous integration compiles and runs the mocked suite, and it does not claim to have tested real pointer behaviour.

## Test host

The suite targets `net48` and runs in an x64 host, which `.runsettings` selects. `Directory.Build.props` names that file through `RunSettingsFilePath`, because `dotnet test` does not discover it automatically.
