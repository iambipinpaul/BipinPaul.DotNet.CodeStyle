# BipinPaul.DotNet.CodeStyle

[![NuGet](https://img.shields.io/nuget/v/BipinPaul.DotNet.CodeStyle.svg)](https://www.nuget.org/packages/BipinPaul.DotNet.CodeStyle/)

A reusable .NET global tool (`csharp-style`) that makes any C# repo's formatting
**explicit, deterministic, and LLM-agent-friendly** — and keeps it that way with
one command.

It runs two ordered passes over your code:

1. **ReSharper `cleanupcode`** (the *free* CLI, no license) with a `ReorderOnly`
   profile — reorders type members to match StyleCop `SA1201/1202/1203`
   (kind → access → constants). `dotnet format` cannot reorder members; this
   fills that gap.
2. **`dotnet format --severity info`** — whitespace, `var`→explicit types,
   always-braces, file-scoped namespaces, and the modern-C# rules your
   `.editorconfig` defines at `suggestion` level.

Order matters: reorder first, format last, so `dotnet format` always gets the
final word on whitespace.

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

Existing files are never clobbered (it skips or merges; use `--force` to
overwrite `.editorconfig`/`.DotSettings`).

### `run` flags
| flag | effect |
|------|--------|
| `--all` / `-a` | whole solution instead of changed files |
| `--base <ref>` | files changed vs a git ref |
| `--list` / `-l` | list files, don't run |
| `--no-reorder` | skip the cleanupcode pass (format only) |
| `--exclude <glob>` | exclude paths (repeatable; merged with `.csharp-style.json`) |
| `--solution <f>` | force a specific `.sln`/`.slnx` |

### Agents vs. humans

The scaffolded `AGENTS.md` tells AI agents to run `csharp-style run --no-reorder`
— the format pass only: fast, no ReSharper CLI, and safe to repeat. Member
ordering is left to the build/CI (StyleCop), so agents don't pay the heavier
reorder cost on every run. To also reorder locally, run `csharp-style run` with
no flag — it's thorough but loads the whole solution, so it takes a while.

## How it adapts to any repo
- Auto-detects the solution (`*.slnx` preferred, else `*.sln`).
- Derives the in-solution **project directories** from the solution file, so
  changed-file scoping works without hardcoded folder names.
- Reads excludes from `.csharp-style.json` so per-repo quirks (e.g. an isolated
  Fallout `build/` project) stay out of the pipeline.

## Known limitation
`SA1204` (static-before-instance *within* an access group) is shipped as a
**suggestion**, not a warning: ReSharper's reorderer can only sort
instance-first, so static-first within an access group isn't auto-enforced
without an unwieldy access×static layout. Everything else (kind, access,
constants) is auto-fixed.
