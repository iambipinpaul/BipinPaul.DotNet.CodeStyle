# BipinPaul.DotNet.CodeStyle

[![NuGet](https://img.shields.io/nuget/v/BipinPaul.DotNet.CodeStyle.svg)](https://www.nuget.org/packages/BipinPaul.DotNet.CodeStyle/)

A reusable .NET global tool (`csharp-style`) that makes any C# repo's formatting
**explicit, deterministic, and LLM-agent-friendly** — and keeps it that way with
one command.

It runs three ordered passes over the same selected files:

1. **`dotnet format --severity info`** — whitespace, `var`→explicit types,
   always-braces, file-scoped namespaces, and the modern-C# rules your
   `.editorconfig` defines at `suggestion` level.
2. **ReSharper `cleanupcode`** (the free CLI) with a `ReorderOnly` profile —
   reorders the resulting members to match StyleCop `SA1201/1202/1203`.
3. **`dotnet format whitespace`** — applies final spacing without creating
   members or changing their modifiers again.

Analyzer fixes can add members, such as generated regex methods. Running them
before ordering prevents those additions from invalidating the final layout.
Review the diff and run the repository's required build and tests afterward.

## Install

Available on [NuGet](https://www.nuget.org/packages/BipinPaul.DotNet.CodeStyle/):

```bash
# global tool
dotnet tool install --global BipinPaul.DotNet.CodeStyle

# or as a repo-local tool
dotnet new tool-manifest        # if you don't have one
dotnet tool install BipinPaul.DotNet.CodeStyle
```

### Build / pack / publish locally (Fallout)

```bash
./build.ps1 Pack     # Clean -> Restore -> Compile -> Pack  (artifacts/Packages)
./build.ps1 Push --NuGetPAT <key>   # also pushes to nuget.org

# install the freshly packed build to test it
dotnet tool install --global --add-source ./artifacts/Packages BipinPaul.DotNet.CodeStyle
```

## Use

Local builds keep outputs in `bin/<os>/` and intermediates in `obj/<os>/`, using
`windows`, `macos`, or `linux`. This lets one checkout retain outputs from each
operating system without mixing generated source or restore assets. With
`RUNNING_IN_CONTAINER=true`, the existing per-project cache under
`/tmp/dotnet-build/` takes precedence. The build runner and isolated ordering
fixtures retain their own build settings.

For scripts that need the built DLL, query its evaluated path instead of
assuming a directory layout:

```bash
dotnet msbuild src/BipinPaul.DotNet.CodeStyle.csproj -nologo -getProperty:TargetPath -property:Configuration=Release
```

```bash
cd your-csharp-repo

csharp-style init            # scaffold config (run once per repo)
dotnet tool restore          # install the ReSharper CLI the tool needs
csharp-style run --all       # normalize the whole solution once (big diff)
csharp-style run             # afterwards: format changed files only
```

### `init` scaffolds
- `.editorconfig` — the agent-optimized ruleset (explicit types, braces,
  naming, modern C#, documented severity model)
- `<solution>.DotSettings` — the `ReorderOnly` cleanup profile + a member layout
  aligned to StyleCop (named to match your `.slnx`/`.sln` so ReSharper
  auto-loads it)
- `Directory.Build.props` — adds `StyleCop.Analyzers` (member-ordering rules) and
  `EnforceCodeStyleInBuild` (runs the `.editorconfig` rules at build, not just the IDE)
- `.config/dotnet-tools.json` — pins the free `JetBrains.ReSharper.GlobalTools`
- `.gitattributes` — cross-platform LF line endings
- `AGENTS.md` — concise C# conventions for AI agents (merged, your own content kept)
- `.csharp-style.json` — optional exclude globs (auto-fills `build/**` if it
  detects an isolated build project)

An existing unmanaged `.editorconfig` is preserved unchanged. Review the
template before using `init --force` to replace it. Tool-managed blocks update
in place, preserving content outside their markers. Existing older files that
already combine custom rules with a managed baseline still need a review of
their overlapping rules; initialization does not guess which custom values
should be removed.

For `.DotSettings`, initialization adds missing keys independently and upgrades
the previously shipped generated layout. Custom layout/profile values and
unrelated settings are preserved and reported for review. `--force` replaces
the whole settings file, so use it only after reviewing existing customizations.

`run` uses the ReSharper version declared in the repository's local manifest,
including architecture-specific installation on Windows ARM64. It fails before
formatting if the manifest, version pin, or settings file is missing; it does
not select a newer ReSharper release automatically.
New initialization pins the tested ReSharper 2026.2.3.1 release; existing pins
are preserved.

### `run` flags
| flag | effect |
|------|--------|
| `--all` / `-a` | whole solution instead of changed files |
| `--base <ref>` | files changed vs a git ref |
| `--list` / `-l` | list files, don't run |
| `--no-reorder` | skip the cleanupcode pass (format only) |
| `--exclude <glob>` | exclude paths (repeatable; merged with `.csharp-style.json`) |
| `--solution <f>` | force a specific `.sln`/`.slnx` |

Unknown arguments, missing option values, and options for another command exit
with code 2 before files are changed or child tools are started. For example,
`csharp-style --version` is unsupported and reports a usage error. Use `--help`
or `-h` for usage information.

### Agents vs. humans

The scaffolded `AGENTS.md` tells agents to follow repository instructions and
reuse existing authorization for formatting, builds, and tests. When permitted,
agents run `csharp-style run` after edits, before verification. Humans and agents
use the same complete pipeline.
Use `--no-reorder` only when deliberately skipping member ordering; it applies
code fixes and whitespace without preparing or invoking ReSharper.

Native failures stop the pipeline and preserve their exit code. An explicit
code-fixer exception also makes the command fail when `dotnet format` itself
exits zero. Diagnostics without an automatic fix remain a manual-review summary.

## How it adapts to any repo
- Auto-detects the solution (`*.slnx` preferred, else `*.sln`).
- Derives the in-solution **project directories** from the solution file, so
  changed-file scoping works without hardcoded folder names.
- Reads excludes from `.csharp-style.json` so per-repo quirks (e.g. an isolated
  Fallout `build/` project) stay out of the pipeline.

## Member layout and limits

The default layout uses explicit StyleCop access ordering, combines constants
and fields with access taking priority, and distinguishes nested record structs
from record classes. Regions inside reorderable types are removed so that they
cannot trap properties or methods in an incompatible group. The template
preserves struct/record-struct internals and types marked with layout or
`NoReorder` attributes. Strict static-initializer dependency handling remains
enabled. These safeguards do not prove behavior equivalence for arbitrary
initializer side effects; inspect member moves in the diff.

`SA1204` (static-before-instance *within* an access group) is shipped as a
**suggestion**, not a warning: ReSharper's reorderer can only sort
instance-first, so static-first within an access group isn't auto-enforced
without an unwieldy access×static layout. Custom settings and types excluded for
safety can still require manual ordering.

## Regression checks

Build the tool and follow [the CLI regression harness instructions](tests/README.md).
The default suite simulates child tool commands without installing packages or
formatting repositories. Add `--real-tools` to verify the full pipeline against
the pinned formatters, StyleCop ordering, runtime invariants, and idempotence.
