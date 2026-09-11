# Releases

## What a release contains

| File | What it is |
| --- | --- |
| `MouseJiggler-<version>-windows-x64-setup.exe` | Per-user installer |
| `MouseJiggler-<version>-windows-x64-portable.zip` | Portable application |
| `SHA256SUMS.txt` | A checksum for every other file above |
| `artifact-manifest.json` | Filename, size, checksum, source commit and build SDK |

Artifacts are unsigned. Verify the checksum before running the installer.

Every file on the release page has a line in `SHA256SUMS.txt`, the manifest included.
The checksums file itself is the one exception, because a checksums file cannot
record its own checksum.

## How a release is made

The release workflow is manual. It validates the tag, requires the commit to be reachable
from `main`, rebuilds and retests from that exact commit, packages, and creates a **draft**
release. Nothing is published automatically.

Publishing the draft is a separate, deliberate act. It requires the Windows acceptance record
for the same commit and the same artifact hashes, with no unresolved failures among the
must-pass rows. Rerunning the workflow against a tag that already has a release is refused
rather than allowed to replace artifacts behind a version somebody may already have
downloaded.

## The scripts

CI runs these rather than its own copy of the same commands, so a green run on a desk and a
green run on a build agent mean the same thing.

| Script | What it does |
| --- | --- |
| `scripts/build.ps1` | Restores with the lock enforced, builds Release, checks the shipped payload against the allowlist |
| `scripts/test.ps1` | Runs the suite. `-LiveInput` enables the tests that move the real pointer; `-Repeat` runs it several times, which is how the named pipe and mutex tests earn their keep |
| `scripts/install-inno.ps1` | Fetches the pinned Inno Setup from its upstream release, checking the recorded size, SHA-256 and publisher signature before running any of it |
| `scripts/package.ps1` | Builds the portable ZIP, the installer, `artifact-manifest.json` and `SHA256SUMS.txt`, in that order, so the checksums cover the manifest. Nothing may write into `artifacts/` after it. `-RequireInstaller` turns a missing compiler from a warning into a failure, which is what CI and the release workflow use |
| `scripts/verify-package.ps1` | Checks what is inside the artifacts, and that every released artifact carries exactly one checksum |
| `scripts/release-files.ps1` | The one definition of which files in `artifacts/` are released, dot-sourced by `package.ps1` and `verify-release.ps1` so the two cannot disagree |
| `scripts/verify-release.ps1` | Checks the release around them: checksums, version stamp, icon, reproducibility, tag and clean tree |
| `scripts/verify-acceptance.ps1` | Runs the issue #14 rows this kind of machine can answer: one engine per session across both distributions, a damaged config file, no sockets at runtime, and a Windows-disabled startup entry surviving an upgrade |
| `scripts/verify-lifecycle.ps1` | Runs a portable copy, installs over it, upgrades over a running copy, uninstalls, and checks that unrelated registry values and files survive |
| `scripts/measure-runtime.ps1` | Measures the runtime gates from issue #14 that one machine can answer: launch time, CPU, private bytes, handle growth, and what survives an abrupt kill |
| `scripts/build-icon.ps1` | Regenerates `assets/MouseJiggler.ico` from the artwork drawn in code |
| `scripts/capture-screenshot.ps1` | Captures the README image with `PrintWindow`, never a screen grab |

## Reproducibility, and its boundary

Two clean builds of the same commit produce identical managed assemblies and an identical
executable, and CI checks that on every run. Deterministic compilation makes the output
content-derived rather than dependent on the path it was built in or the time it was built at.
Verified on Windows 10 19045 across three clean builds.

The executable is compared as well as the libraries. It is what people download, and it
carries the embedded icon resource that `PathMap` cannot reach inside, so leaving it out would
have meant not checking the one file that matters most.

That guarantee covers the application binaries. It does not extend to the installer container,
because the installer tool embeds its own timestamps. The honest statement is that the
application binaries are reproducible and the installer wrapper is not, rather than a blanket
claim of byte-identical releases.
