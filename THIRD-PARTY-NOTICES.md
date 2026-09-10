# Third-party notices

Mouse Jiggler ships no third-party code.

The application binaries consist of `MouseJiggler.exe`, `MouseJiggler.Core.dll` and
`MouseJiggler.Windows.dll`, all built from this repository and licensed under the MIT
licence in [LICENSE](LICENSE). Everything else the app uses at runtime is part of
.NET Framework 4.8, which is a component of Windows.

The following packages are used to build and test the project. None of them is
redistributed, and none is present in a released artifact.

| Package | Version | Used for |
| --- | --- | --- |
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 | Reference assemblies so net48 builds without a Developer Pack installed |
| Microsoft.NET.Test.Sdk | 17.14.1 | Test host |
| xunit | 2.9.3 | Test framework |
| xunit.runner.visualstudio | 3.1.5 | Test adapter |

Inno Setup 6.7.3 builds the installer. It is a build tool, not a component of the
product, and is not redistributed here.
