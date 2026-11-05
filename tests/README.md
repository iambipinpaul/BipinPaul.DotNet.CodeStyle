# CLI regression harness

This dependency-free .NET 10 console project tests the built tool as a black box.
It requires Git and a normal framework-dependent build with its apphost present.
Pass absolute paths to the real `dotnet` host and the built CodeStyle tool DLL:

```powershell
dotnet run --project tests/BipinPaul.DotNet.CodeStyle.RegressionTests -- --dotnet 'C:/Program Files/dotnet/dotnet.exe' --tool 'C:/absolute/path/BipinPaul.DotNet.CodeStyle.dll'
```

For an already built harness, avoid rebuilding with:

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' 'C:/absolute/path/BipinPaul.DotNet.CodeStyle.RegressionTests.dll' --dotnet 'C:/Program Files/dotnet/dotnet.exe' --tool 'C:/absolute/path/BipinPaul.DotNet.CodeStyle.dll'
```

The harness copies its apphost and runtime files into a temporary PATH directory,
renames that apphost to `dotnet`, and logs the tool's child commands as JSON arrays.
The tool apphost is copied beside the fake host and launched there on every
platform, because .NET searches the parent executable directory before PATH.
Executable permissions are preserved on Unix. Child formatting/tool
installation commands are simulated; the tested tool never invokes real formatters
or installs packages. Fixtures and their temporary Git commits stay under a unique
directory in the system temporary folder and are removed when the harness exits.

For `--real-tools`, prefer the already-built harness command above. This keeps
`dotnet run` from carrying SDK-specific MSBuild settings into the .NET 10
fixtures when the machine also has a newer SDK installed.

Add `--real-tools` to also run the actual pinned ReSharper and .NET formatters on
the ordering fixture, compile it with SA1201–SA1203 as errors, execute its layout
and initialization invariants, and check that a second cleanup changes no source.
This opt-in selection may restore the manifest's exact ReSharper package and the
fixture's pinned StyleCop package. It uses an isolated .NET 10 fixture and does
not change the installed CodeStyle tool or the source checkout's settings.

The process exits nonzero if any case fails and prints a pass/fail count. The nearby
`Directory.Build.props` imports the repository's output-path settings for the
harness. The ordering fixture keeps its own empty props file so its standalone
output layout and analyzer setup remain isolated.

Cases cover pinning, restore fallback, captured file scope and exclusions, stage
sequencing and failure exits, concurrent fixer diagnostics, EditorConfig ownership
and severity values, and XML-safe DotSettings migration. Template resources are
read from the tool DLL under test. Older DLLs without the legacy template resource
use their original template as the legacy fixture; the migration test independently
requires explicit access precedence and region removal.

Razor cases cover changed, untracked, base-reference and whole-solution selection;
list-only behavior; exclusions; mixed C#/Razor scoping; failure ordering;
encoding and newline preservation; conservative syntax boundaries; and
idempotence. The real-tool selection also compiles a Razor component library
before and after cleanup and verifies a second run leaves both component and
imports files byte-identical.
