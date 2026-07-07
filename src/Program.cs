// csharp-style — a reusable, two-pass C# formatter + config scaffolder.
//
//   Pass 1: ReSharper `cleanupcode` (free CLI) with a ReorderOnly profile,
//           reordering type members to match StyleCop SA1201/1202/1203.
//   Pass 2: `dotnet format --severity info`, honoring .editorconfig.
//
// Commands:
//   csharp-style init                 scaffold .editorconfig, StyleCop props,
//                                      ReSharper manifest, <sln>.DotSettings,
//                                      .gitattributes, AGENTS.md into the repo
//   csharp-style run                  format changed files (default command)
//   csharp-style run --all            format the entire solution
//   csharp-style run --base <ref>     format files changed vs a git ref
//   csharp-style run --list           list the files, do not run
//   csharp-style run --no-reorder     skip the cleanupcode pass
//   csharp-style ... --exclude <glob> exclude paths (repeatable; also read from
//                                      .csharp-style.json "exclude": [...])
//   csharp-style ... --solution <f>   force a specific .sln/.slnx
//
// The runner auto-detects the solution (*.slnx preferred, else *.sln) and the
// in-solution project directories, so it works in any repo without edits.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";

// --- Parse flags -------------------------------------------------------------
string? baseRef = null;
string? explicitSolution = null;
bool listOnly = false;
bool noReorder = false;
bool all = false;
bool force = false;
List<string> cliExcludes = [];

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--base" or "-b" when i + 1 < args.Length: baseRef = args[++i]; break;
        case "--solution" or "-s" when i + 1 < args.Length: explicitSolution = args[++i]; break;
        case "--exclude" or "-e" when i + 1 < args.Length: cliExcludes.Add(args[++i]); break;
        case "--list" or "-l": listOnly = true; break;
        case "--no-reorder": noReorder = true; break;
        case "--all" or "-a": all = true; break;
        case "--force" or "-f": force = true; break;
        case "--help" or "-h": PrintHelp(); return 0;
    }
}

string repo = Directory.GetCurrentDirectory();

try
{
    return command switch
    {
        "init" => Init(repo, explicitSolution, force),
        "run" or "format" => Run(repo, explicitSolution, baseRef, all, listOnly, noReorder, cliExcludes),
        "help" => Pass(PrintHelp),
        _ => Unknown(command),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// ============================================================================
// init
// ============================================================================
int Init(string repoRoot, string? sln, bool overwrite)
{
    string? solution = sln ?? FindSolution(repoRoot);
    if (solution is null)
    {
        Console.Error.WriteLine("No .slnx/.sln found here. Pass --solution <file> or run from the repo root.");
        return 1;
    }

    string settingsFile = solution + ".DotSettings";
    Console.WriteLine($"Scaffolding style config for solution: {solution}");

    // .editorconfig — merged: our block goes last and wins (editorconfig is
    // last-key-wins), so existing custom rules survive but our standard prevails.
    MergeManaged(Path.Combine(repoRoot, ".editorconfig"), Template("editorconfig.template"), splitAtFirstSection: true, overwrite);
    // .gitattributes — merged the same way (gitattributes is last-match-wins).
    MergeManaged(Path.Combine(repoRoot, ".gitattributes"), Template("gitattributes.template"), splitAtFirstSection: false, overwrite);
    // AGENTS.md — C# conventions for AI agents. Merged like the others, but with
    // HTML-comment markers so the managed block stays invisible in rendered
    // Markdown and any hand-written guidance outside the block is preserved.
    MergeManaged(Path.Combine(repoRoot, "AGENTS.md"), Template("AGENTS.md.template"), splitAtFirstSection: false, overwrite, "<!--", " -->");
    // <solution>.DotSettings  (ReorderOnly profile + StyleCop layout)
    MergeDotSettings(Path.Combine(repoRoot, settingsFile), Template("solution.DotSettings.template"), overwrite);
    // Directory.Build.props  (StyleCop package)
    MergeDirectoryBuildProps(Path.Combine(repoRoot, "Directory.Build.props"), Template("Directory.Build.props.template"));
    // .config/dotnet-tools.json  (ReSharper CLI)
    MergeToolManifest(Path.Combine(repoRoot, ".config", "dotnet-tools.json"));

    // .csharp-style.json  (optional excludes; pre-fill build/** if an isolated build project exists)
    string cfg = Path.Combine(repoRoot, ".csharp-style.json");
    if (!File.Exists(cfg))
    {
        bool hasIsolatedBuild = File.Exists(Path.Combine(repoRoot, "build", "Directory.Build.props"));
        string[] ex = hasIsolatedBuild ? ["build/**"] : [];
        File.WriteAllText(cfg, JsonSerializer.Serialize(new { exclude = ex }, JsonOpts()) + "\n");
        Console.WriteLine($"  + .csharp-style.json  (exclude: [{string.Join(", ", ex)}])");
    }

    Console.WriteLine();
    Console.WriteLine("Done. Next steps:");
    Console.WriteLine("  1. dotnet tool restore                         # install the ReSharper CLI (auto-detects arch)");
    Console.WriteLine("  2. csharp-style run --all                      # normalize the whole repo once");
    Console.WriteLine("  3. then csharp-style run                       # format changed files going forward");
    Console.WriteLine();
    Console.WriteLine("Note: member ordering (SA1201/1202/1203) is enforced at build via StyleCop.");
    Console.WriteLine("Review the generated .editorconfig and adjust to your team's conventions.");
    return 0;
}

// ============================================================================
// run
// ============================================================================
int Run(string repoRoot, string? sln, string? @ref, bool whole, bool list, bool skipReorder, List<string> excludesCli)
{
    string? solution = sln ?? FindSolution(repoRoot);
    if (solution is null)
    {
        Console.Error.WriteLine("No .slnx/.sln found here. Pass --solution <file>.");
        return 1;
    }
    string settingsFile = solution + ".DotSettings";
    List<string> excludes = LoadExcludes(repoRoot, excludesCli);

    List<string> files = [];
    if (whole)
    {
        Console.WriteLine($"Mode: ALL files in {solution}"
            + (excludes.Count > 0 ? $" (excluding {string.Join(", ", excludes)})" : ""));
    }
    else
    {
        HashSet<string> projectDirs = ProjectDirs(repoRoot, solution);
        files = ChangedCsFiles(@ref)
            .Where(File.Exists)
            .Where(f => InAnyProjectDir(f, projectDirs))
            .Where(f => !IsExcluded(f, excludes))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("No changed solution .cs files to format.");
            return 0;
        }
        Console.WriteLine($"{files.Count} changed .cs file(s):");
        files.ForEach(f => Console.WriteLine($"  {f}"));
    }

    if (list)
        return 0;

    // --- Pass 1: reorder members (cleanupcode) -------------------------------
    int reorderExit = 0;
    if (!skipReorder)
    {
        Console.WriteLine("[1/2] Reordering members (cleanupcode ReorderOnly)...");
        if (EnsureReSharperTool() is var r && r != 0)
        {
            Console.Error.WriteLine($"ReSharper tool install failed ({r}). Did you run `csharp-style init`?");
            return r;
        }

        List<string> a = ["jb", "cleanupcode", solution, "--profile=ReorderOnly",
                          $"--settings={settingsFile}", "--no-build"];
        if (whole)
        {
            if (excludes.Count > 0) a.Add($"--exclude={string.Join(';', excludes)}");
        }
        else
        {
            a.Add($"--include={string.Join(';', files)}");
        }

        reorderExit = RunInherit("dotnet", [.. a]);
        if (reorderExit != 0)
            Console.WriteLine($"cleanupcode exited {reorderExit}; continuing to formatting.");
    }

    // --- Pass 2: dotnet format ----------------------------------------------
    Console.WriteLine("[2/2] Running dotnet format --severity info...");
    List<string> fa = ["format", solution, "--severity", "info"];
    if (whole)
    {
        foreach (string e in excludes) { fa.Add("--exclude"); fa.Add(e); }
    }
    else
    {
        fa.Add("--include");
        fa.AddRange(files);
    }

    int formatExit = RunDotnetFormat([.. fa], out string manualSummary);
    if (formatExit != 0)
    {
        Console.WriteLine($"dotnet format exited {formatExit}.");
    }
    else
    {
        Console.WriteLine(reorderExit == 0 ? "Done." : $"Formatted, but cleanupcode exited {reorderExit}.");
        if (manualSummary.Length > 0)
        {
            // Framed explicitly so humans AND LLM agents read it as informational,
            // not a failure: these rules simply have no automatic code fix.
            Console.WriteLine($"Note: formatting SUCCEEDED. These rules have no auto-fix and need a manual edit (expected, not an error): {manualSummary}.");
        }
    }

    return formatExit != 0 ? formatExit : reorderExit;
}

// ============================================================================
// Solution / project discovery
// ============================================================================
string? FindSolution(string repoRoot)
{
    string[] slnx = Directory.GetFiles(repoRoot, "*.slnx");
    string[] sln = Directory.GetFiles(repoRoot, "*.sln");
    string[] pick = slnx.Length > 0 ? slnx : sln;
    return pick.Length == 1 ? Path.GetFileName(pick[0])
         : pick.Length == 0 ? null
         : throw new InvalidOperationException(
             $"Multiple solutions found ({string.Join(", ", pick.Select(Path.GetFileName))}); pass --solution.");
}

HashSet<string> ProjectDirs(string repoRoot, string solution)
{
    HashSet<string> dirs = new(StringComparer.OrdinalIgnoreCase);
    string text = File.ReadAllText(Path.Combine(repoRoot, solution));

    // .slnx: <Project Path="rel\path.csproj" />   |   .sln: ... = "Name", "rel\path.csproj", "{guid}"
    IEnumerable<string> paths = solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
        ? Regex.Matches(text, "Path\\s*=\\s*\"([^\"]+\\.csproj)\"").Select(m => m.Groups[1].Value)
        : Regex.Matches(text, "\"([^\"]+\\.csproj)\"").Select(m => m.Groups[1].Value);

    foreach (string p in paths)
    {
        string norm = p.Replace('\\', '/');
        string dir = Path.GetDirectoryName(norm)?.Replace('\\', '/') ?? "";
        if (dir.Length > 0) dirs.Add(dir.TrimEnd('/') + "/");
    }
    return dirs;
}

bool InAnyProjectDir(string file, HashSet<string> dirs)
{
    string f = file.Replace('\\', '/');
    return dirs.Count == 0 || dirs.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase));
}

IEnumerable<string> ChangedCsFiles(string? @ref)
{
    if (@ref is not null)
        return Git("diff", "--name-only", "--diff-filter=ACMR", @ref, "--", "*.cs");

    return Git("diff", "--name-only", "--cached", "--diff-filter=ACMR", "--", "*.cs")
        .Concat(Git("diff", "--name-only", "--diff-filter=ACMR", "--", "*.cs"))
        .Concat(Git("ls-files", "--others", "--exclude-standard", "--", "*.cs"));
}

// ============================================================================
// Excludes
// ============================================================================
List<string> LoadExcludes(string repoRoot, List<string> cli)
{
    List<string> result = [.. cli];
    string cfg = Path.Combine(repoRoot, ".csharp-style.json");
    if (File.Exists(cfg))
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (doc.RootElement.TryGetProperty("exclude", out JsonElement ex) && ex.ValueKind == JsonValueKind.Array)
                result.AddRange(ex.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
        }
        catch (Exception ex) { Console.Error.WriteLine($"warning: ignoring bad .csharp-style.json ({ex.Message})"); }
    }
    return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

bool IsExcluded(string file, List<string> globs)
{
    string f = file.Replace('\\', '/');
    return globs.Any(g => GlobToRegex(g).IsMatch(f));
}

Regex GlobToRegex(string glob)
{
    string g = glob.Replace('\\', '/');
    StringBuilder sb = new("^");
    for (int i = 0; i < g.Length; i++)
    {
        if (g[i] == '*' && i + 1 < g.Length && g[i + 1] == '*') { sb.Append(".*"); i++; }
        else if (g[i] == '*') sb.Append("[^/]*");
        else if (g[i] == '?') sb.Append('.');
        else sb.Append(Regex.Escape(g[i].ToString()));
    }
    sb.Append("$");
    return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
}

// ============================================================================
// Scaffolding helpers
// ============================================================================
// Merge a template into a line-based config (.editorconfig / .gitattributes) via
// a delimited "managed block" kept at the END of the file. Both formats are
// last-rule-wins, so our block overrides overlapping keys while leaving the
// user's other settings intact. Re-running updates the block in place.
void MergeManaged(string path, string template, bool splitAtFirstSection, bool overwrite,
                  string commentPrefix = "#", string commentSuffix = "")
{
    string begin = commentPrefix + " >>> csharp-style (managed — re-init overwrites this block) >>>" + commentSuffix;
    string end = commentPrefix + " <<< csharp-style (managed) <<<" + commentSuffix;
    string name = Path.GetFileName(path);

    // For .editorconfig, keep the preamble (header + `root = true`) out of the
    // managed block so we never force `root` onto an existing file.
    string preamble = "", body = template.Replace("\r\n", "\n");
    if (splitAtFirstSection)
    {
        int i = body.IndexOf("\n[", StringComparison.Ordinal);
        if (i >= 0) { preamble = body[..(i + 1)]; body = body[(i + 1)..]; }
    }

    string block = begin + "\n" + body.Trim('\n') + "\n" + end;
    bool existed = File.Exists(path);

    if (!existed || overwrite)
    {
        string fresh = (preamble.Length > 0 ? preamble.TrimEnd('\n') + "\n\n" : "") + block + "\n";
        File.WriteAllText(path, fresh);
        Console.WriteLine($"  {(existed ? "~" : "+")} {name}{(existed ? " (overwritten)" : "")}");
        return;
    }

    string existing = File.ReadAllText(path).Replace("\r\n", "\n");
    if (existing.Contains(begin) && existing.Contains(end))
    {
        existing = Regex.Replace(existing, Regex.Escape(begin) + ".*?" + Regex.Escape(end), _ => block, RegexOptions.Singleline);
        File.WriteAllText(path, existing);
        Console.WriteLine($"  ~ {name} (updated managed block)");
    }
    else
    {
        File.WriteAllText(path, existing.TrimEnd('\n') + "\n\n" + block + "\n");
        Console.WriteLine($"  ~ {name} (merged: appended managed block)");
    }
}

void MergeDotSettings(string path, string template, bool overwrite)
{
    string name = Path.GetFileName(path);
    if (!File.Exists(path)) { File.WriteAllText(path, template); Console.WriteLine($"  + {name}"); return; }
    if (overwrite) { File.WriteAllText(path, template); Console.WriteLine($"  ~ {name} (overwritten)"); return; }

    string existing = File.ReadAllText(path);
    if (existing.Contains("CSharpFileLayoutPatterns/Pattern/@EntryValue")
        && existing.Contains("Profiles/=ReorderOnly"))
    {
        Console.WriteLine($"  = {name} (ReorderOnly + layout already present, skipped)");
        return;
    }
    // Inject our two keys before the closing tag.
    Match keys = Regex.Match(template, "(?s)<s:String.*</s:String>");
    if (keys.Success && existing.Contains("</wpf:ResourceDictionary>"))
    {
        existing = existing.Replace("</wpf:ResourceDictionary>", "\t" + keys.Value + "\r\n</wpf:ResourceDictionary>");
        File.WriteAllText(path, existing);
        Console.WriteLine($"  ~ {name} (injected ReorderOnly profile + layout)");
    }
    else
    {
        Console.WriteLine($"  ! {name} exists but could not be merged; add the template keys manually.");
    }
}

void MergeDirectoryBuildProps(string path, string template)
{
    if (!File.Exists(path)) { File.WriteAllText(path, template); Console.WriteLine("  + Directory.Build.props"); return; }
    string existing = File.ReadAllText(path);
    int idx = existing.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
    if (idx < 0)
    {
        Console.WriteLine("  ! Directory.Build.props exists but has no </Project>; add StyleCop + EnforceCodeStyleInBuild manually.");
        return;
    }

    // Ensure both pieces independently, so an existing file that already has one
    // (e.g. StyleCop) still gets the other (EnforceCodeStyleInBuild) added.
    StringBuilder insert = new();
    List<string> added = [];

    if (!existing.Contains("EnforceCodeStyleInBuild"))
    {
        insert.Append("""
              <PropertyGroup>
                <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
              </PropertyGroup>

            """);
        added.Add("EnforceCodeStyleInBuild");
    }

    if (!existing.Contains("StyleCop.Analyzers"))
    {
        insert.Append("""
              <ItemGroup>
                <PackageReference Include="StyleCop.Analyzers" Version="1.2.0-beta.556">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
                </PackageReference>
              </ItemGroup>

            """);
        added.Add("StyleCop.Analyzers");
    }

    if (insert.Length == 0)
    {
        Console.WriteLine("  = Directory.Build.props (StyleCop + code-style already present, skipped)");
        return;
    }

    existing = existing.Insert(idx, insert.ToString());
    File.WriteAllText(path, existing);
    Console.WriteLine($"  ~ Directory.Build.props (added {string.Join(" + ", added)})");
}

void MergeToolManifest(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (!File.Exists(path))
    {
        File.WriteAllText(path, Template("dotnet-tools.json.template"));
        Console.WriteLine("  + .config/dotnet-tools.json");
        return;
    }
    try
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        JsonObject tools = root["tools"] as JsonObject ?? [];
        if (tools.ContainsKey("jetbrains.resharper.globaltools"))
        {
            Console.WriteLine("  = .config/dotnet-tools.json (ReSharper already present, skipped)");
            return;
        }
        tools["jetbrains.resharper.globaltools"] = new JsonObject
        {
            ["version"] = "2026.1.3",
            ["commands"] = new JsonArray("jb"),
            ["rollForward"] = false,
        };
        root["version"] ??= 1;
        root["isRoot"] ??= true;
        root["tools"] = tools;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine("  ~ .config/dotnet-tools.json (added ReSharper CLI)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ! .config/dotnet-tools.json exists but couldn't be merged ({ex.Message}); add jetbrains.resharper.globaltools manually.");
    }
}

// ============================================================================
// Process + resource helpers
// ============================================================================
IEnumerable<string> Git(params string[] arguments)
{
    ProcessStartInfo psi = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in arguments) psi.ArgumentList.Add(a);
    using Process p = Process.Start(psi)!;
    // Drain both streams concurrently. Reading one to EOF before the other
    // deadlocks if the child fills the second pipe's buffer first -- e.g. git's
    // per-file "LF will be replaced by CRLF" warnings on stderr when many files
    // have changed, which is why `run` hung on large staged sets.
    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
    Task<string> errTask = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    string output = outTask.Result, error = errTask.Result;
    if (p.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({p.ExitCode}): {error}");
    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

int RunInherit(string file, string[] arguments)
{
    ProcessStartInfo psi = new(file) { UseShellExecute = false };
    foreach (string a in arguments) psi.ArgumentList.Add(a);
    using Process p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
}

// Installs the ReSharper local tool with the correct --arch flag so it works
// on Windows ARM64 (where dotnet tool restore alone may pick the wrong RID).
// Falls back to `dotnet tool restore` if the explicit install fails (e.g. the
// tool is already installed — "install" returns non-zero for that case).
int EnsureReSharperTool()
{
    string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    int r = RunInherit("dotnet", ["tool", "install", "JetBrains.ReSharper.GlobalTools",
                                  "--local", "--arch", arch]);
    if (r == 0) return 0;
    return RunInherit("dotnet", ["tool", "restore"]);
}

// Runs `dotnet format`, suppressing its noisy per-issue "Unable to fix X" lines
// (and the generic workspace-load warning), and instead returns a single tidy
// summary of the rule codes that have no auto-fix. This keeps the output clean
// and unambiguous so an LLM agent doesn't mistake "Unable to fix" for a failure.
int RunDotnetFormat(string[] arguments, out string manualSummary)
{
    Dictionary<string, int> unfixable = new(StringComparer.OrdinalIgnoreCase);
    ProcessStartInfo psi = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in arguments) psi.ArgumentList.Add(a);

    void Handle(string? line)
    {
        if (line is null) return;
        Match m = Regex.Match(line, @"Unable to fix (\S+?)\.");
        if (m.Success)
        {
            unfixable[m.Groups[1].Value] = unfixable.GetValueOrDefault(m.Groups[1].Value) + 1;
            return; // swallow the raw line; summarized at the end
        }
        if (line.Contains("Warnings were encountered while loading the workspace")) return;
        Console.WriteLine(line);
    }

    using Process p = Process.Start(psi)!;
    p.OutputDataReceived += (_, e) => Handle(e.Data);
    p.ErrorDataReceived += (_, e) => Handle(e.Data);
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    p.WaitForExit();

    manualSummary = unfixable.Count == 0
        ? ""
        : string.Join(", ", unfixable
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Value > 1 ? $"{kv.Key} ({kv.Value})" : kv.Key));
    return p.ExitCode;
}

string Template(string name)
{
    Assembly asm = Assembly.GetExecutingAssembly();
    string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
    if (res is null) throw new InvalidOperationException($"embedded template '{name}' not found");
    using Stream s = asm.GetManifestResourceStream(res)!;
    using StreamReader r = new(s);
    return r.ReadToEnd();
}

JsonSerializerOptions JsonOpts() => new() { WriteIndented = true };

int Pass(Action a) { a(); return 0; }
int Unknown(string c) { Console.Error.WriteLine($"unknown command '{c}'. Try: init | run | --help"); return 2; }

void PrintHelp() => Console.WriteLine(
"""
csharp-style — two-pass C# formatter (member reorder + dotnet format) + scaffolder

USAGE
  csharp-style init [--solution <f>] [--force]
  csharp-style run  [--all] [--base <ref>] [--list] [--no-reorder]
                    [--exclude <glob>]... [--solution <f>]

  (run is the default command, so `csharp-style --all` works too.)

WHAT init CREATES
  .editorconfig, .gitattributes, AGENTS.md (C# conventions for AI agents),
  <solution>.DotSettings (ReorderOnly profile + StyleCop member layout),
  Directory.Build.props (StyleCop.Analyzers), .config/dotnet-tools.json (free
  ReSharper CLI), .csharp-style.json (excludes).

EXAMPLES
  csharp-style init                 # scaffold config into this repo
  csharp-style run --all            # normalize the whole solution once
  csharp-style run                  # format changed files (reorder + format)
  csharp-style run --base main      # format files changed vs main
""");
