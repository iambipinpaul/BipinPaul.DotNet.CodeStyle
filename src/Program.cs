// csharp-style — a reusable C# cleanup pipeline + config scaffolder.
//
//   Apply analyzer fixes, reorder the resulting members with ReSharper,
//   then format whitespace without introducing more members.
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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

bool hasExplicitCommand = args.Length > 0 && !args[0].StartsWith('-');
string command = hasExplicitCommand ? args[0] : "run";
if (command is not ("init" or "run" or "format" or "help")) return Unknown(command);
bool isRunCommand = command is "run" or "format";

// --- Parse flags before any repository inspection or mutation ----------------
string? baseRef = null;
string? explicitSolution = null;
bool listOnly = false;
bool noReorder = false;
bool all = false;
bool force = false;
bool showHelp = false;
List<string> cliExcludes = [];

for (int i = hasExplicitCommand ? 1 : 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--base" or "-b" when isRunCommand:
        case "--solution" or "-s" when command != "help":
        case "--exclude" or "-e" when isRunCommand:
            string option = args[i];
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-'))
                return UsageError($"Option '{option}' requires a value.");
            string value = args[++i];
            switch (option)
            {
                case "--base" or "-b": baseRef = value; break;
                case "--solution" or "-s": explicitSolution = value; break;
                case "--exclude" or "-e": cliExcludes.Add(value); break;
            }
            break;
        case "--list" or "-l" when isRunCommand: listOnly = true; break;
        case "--no-reorder" when isRunCommand: noReorder = true; break;
        case "--all" or "-a" when isRunCommand: all = true; break;
        case "--force" or "-f" when command == "init": force = true; break;
        case "--help" or "-h": showHelp = true; break;
        default: return UsageError($"Unknown option or argument '{args[i]}' for '{command}'.");
    }
}

if (showHelp) return Pass(PrintHelp);

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

    // Existing unmanaged preferences belong to the repository. Do not append
    // another complete baseline that silently overrides them.
    MergeEditorConfig(Path.Combine(repoRoot, ".editorconfig"), overwrite);
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

    // Validate prerequisites before any code fix can change source files.
    if (!skipReorder)
    {
        if (!File.Exists(Path.Combine(repoRoot, settingsFile)))
        {
            Console.Error.WriteLine($"Missing {settingsFile}. Run `csharp-style init` and review the generated layout first.");
            return 1;
        }
        int toolExit = EnsureReSharperTool(repoRoot);
        if (toolExit != 0)
        {
            Console.Error.WriteLine($"ReSharper setup failed ({toolExit}); source files have not been changed.");
            return toolExit;
        }
    }

    // Code fixes may introduce members. They must run before final ordering.
    Console.WriteLine("[1/3] Applying dotnet format code fixes (--severity info)...");
    List<string> fa = ["format", solution, "--severity", "info"];
    AddFormatScope(fa);

    int formatExit = RunDotnetFormat([.. fa], out string manualSummary);
    if (formatExit != 0)
    {
        Console.Error.WriteLine($"Code fix pass failed ({formatExit}); review its output and any partial changes.");
        return formatExit;
    }

    if (!skipReorder)
    {
        Console.WriteLine("[2/3] Reordering members (cleanupcode ReorderOnly)...");
        List<string> reorderArguments = ["tool", "run", "jb", "--", "cleanupcode", solution,
            "--profile=ReorderOnly", $"--settings={settingsFile}", "--no-build", "--no-updates"];
        if (whole)
        {
            if (excludes.Count > 0) reorderArguments.Add($"--exclude={string.Join(';', excludes)}");
        }
        else
        {
            reorderArguments.Add($"--include={string.Join(';', files)}");
        }
        int reorderExit = RunInherit("dotnet", [.. reorderArguments]);
        if (reorderExit != 0)
        {
            Console.Error.WriteLine($"Member ordering failed ({reorderExit}).");
            return reorderExit;
        }
    }

    Console.WriteLine("[3/3] Formatting whitespace...");
    List<string> whitespaceArguments = ["format", "whitespace", solution, "--no-restore"];
    AddFormatScope(whitespaceArguments);
    int whitespaceExit = RunInherit("dotnet", [.. whitespaceArguments]);
    if (whitespaceExit != 0)
    {
        Console.Error.WriteLine($"Whitespace formatting failed ({whitespaceExit}).");
        return whitespaceExit;
    }

    Console.WriteLine("Done.");
    if (manualSummary.Length > 0)
        Console.WriteLine($"Manual review is still needed for diagnostics without automatic fixes: {manualSummary}.");
    return 0;

    void AddFormatScope(List<string> arguments)
    {
        if (whole)
        {
            foreach (string exclude in excludes) { arguments.Add("--exclude"); arguments.Add(exclude); }
        }
        else
        {
            arguments.Add("--include");
            arguments.AddRange(files);
        }
    }
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
void MergeEditorConfig(string path, bool overwrite)
{
    if (File.Exists(path) && !overwrite
        && !File.ReadAllText(path).Contains("# >>> csharp-style")
        && !File.ReadAllText(path).Contains("# <<< csharp-style"))
    {
        Console.WriteLine("  = .editorconfig (existing repository settings preserved; review before using init --force)");
        return;
    }
    MergeManaged(path, Template("editorconfig.template"), splitAtFirstSection: true, overwrite);
}

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

    string existing = File.ReadAllText(path);
    string newline = existing.Contains("\r\n") ? "\r\n" : "\n";
    block = block.Replace("\n", newline);
    int beginCount = Regex.Matches(existing, Regex.Escape(begin)).Count;
    int endCount = Regex.Matches(existing, Regex.Escape(end)).Count;
    if (beginCount == 1 && endCount == 1 && existing.IndexOf(begin, StringComparison.Ordinal) < existing.IndexOf(end, StringComparison.Ordinal))
    {
        string updated = Regex.Replace(existing, Regex.Escape(begin) + ".*?" + Regex.Escape(end), _ => block, RegexOptions.Singleline);
        if (updated == existing)
            Console.WriteLine($"  = {name} (managed block already current)");
        else
        {
            File.WriteAllText(path, updated);
            Console.WriteLine($"  ~ {name} (updated managed block)");
        }
    }
    else if (existing.Contains(commentPrefix + " >>> csharp-style") || existing.Contains(commentPrefix + " <<< csharp-style"))
    {
        throw new InvalidDataException($"{name} has incomplete or duplicate csharp-style managed markers; repair them before reinitializing.");
    }
    else
    {
        File.WriteAllText(path, existing.TrimEnd('\r', '\n') + newline + newline + block + newline);
        Console.WriteLine($"  ~ {name} (merged: appended managed block)");
    }
}

void MergeDotSettings(string path, string template, bool overwrite)
{
    string name = Path.GetFileName(path);
    if (!File.Exists(path)) { File.WriteAllText(path, template); Console.WriteLine($"  + {name}"); return; }
    if (overwrite) { File.WriteAllText(path, template); Console.WriteLine($"  ~ {name} (overwritten)"); return; }

    string existing = File.ReadAllText(path);
    XDocument document = XDocument.Parse(existing, LoadOptions.PreserveWhitespace);
    XDocument defaults = XDocument.Parse(template);
    XDocument legacy = XDocument.Parse(Template("solution.DotSettings.legacy.template"));
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    if (document.Root?.Name != defaults.Root!.Name)
        throw new InvalidDataException($"{name} is not a ReSharper resource dictionary; existing settings were preserved.");

    bool changed = false;
    foreach (XElement entry in defaults.Root.Elements())
    {
        string key = entry.Attribute(x + "Key")!.Value;
        List<XElement> matches = document.Root.Elements().Where(e => (string?)e.Attribute(x + "Key") == key).ToList();
        if (matches.Count > 1)
            throw new InvalidDataException($"{name} contains duplicate resource key '{key}'; existing settings were preserved.");
        if (matches.Count == 0)
        {
            document.Root.Add(new XElement(entry));
            changed = true;
            continue;
        }
        XElement current = matches[0];
        if (SameSettingsValue(current.Value, entry.Value)) continue;
        XElement? old = legacy.Root!.Elements().FirstOrDefault(e => (string?)e.Attribute(x + "Key") == key);
        if (old is not null && SameSettingsValue(current.Value, old.Value))
        {
            current.Value = entry.Value;
            changed = true;
        }
        else
        {
            Console.WriteLine($"  ! {name}: custom value for '{key}' preserved; compare it with the current template.");
        }
    }

    if (changed)
    {
        string newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        // File.WriteAllText emits UTF-8; keep the declaration consistent with it.
        if (document.Declaration is not null) document.Declaration.Encoding = "utf-8";
        string declaration = document.Declaration is null ? "" : document.Declaration + newline;
        string updated = declaration + document.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n").Replace("\n", newline);
        if (existing.EndsWith('\n')) updated += newline;
        File.WriteAllText(path, updated);
        Console.WriteLine($"  ~ {name} (added missing settings or upgraded the previous generated layout)");
    }
    else
        Console.WriteLine($"  = {name} (existing settings preserved)");

    bool SameSettingsValue(string left, string right)
    {
        if (left == right) return true;
        try { return XNode.DeepEquals(XElement.Parse(left), XElement.Parse(right)); }
        catch (System.Xml.XmlException) { return false; }
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
        JsonNode defaults = JsonNode.Parse(Template("dotnet-tools.json.template"))!;
        tools["jetbrains.resharper.globaltools"] = defaults["tools"]!["jetbrains.resharper.globaltools"]!.DeepClone();
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
int EnsureReSharperTool(string repoRoot)
{
    string manifestPath = Path.Combine(repoRoot, ".config", "dotnet-tools.json");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine("Missing local tool manifest. Run `csharp-style init` and review the pinned ReSharper version first.");
        return 1;
    }
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
    string? version = null;
    if (manifest.RootElement.TryGetProperty("tools", out JsonElement tools))
    {
        foreach (JsonProperty tool in tools.EnumerateObject())
        {
            if (tool.Name.Equals("jetbrains.resharper.globaltools", StringComparison.OrdinalIgnoreCase)
                && tool.Value.TryGetProperty("version", out JsonElement declaredVersion))
                version = declaredVersion.GetString();
        }
    }
    if (string.IsNullOrWhiteSpace(version))
    {
        Console.Error.WriteLine("The local manifest must pin jetbrains.resharper.globaltools before member ordering can run.");
        return 1;
    }
    string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    int r = RunInherit("dotnet", ["tool", "install", "JetBrains.ReSharper.GlobalTools",
                                  "--local", "--tool-manifest", manifestPath, "--version", version, "--arch", arch]);
    if (r == 0) return 0;
    return RunInherit("dotnet", ["tool", "restore", "--tool-manifest", manifestPath]);
}

// Runs `dotnet format`, suppressing its noisy per-issue "Unable to fix X" lines
// (and the generic workspace-load warning), and instead returns a single tidy
// summary of the rule codes that have no auto-fix. This keeps the output clean
// and unambiguous so an LLM agent doesn't mistake "Unable to fix" for a failure.
int RunDotnetFormat(string[] arguments, out string manualSummary)
{
    ConcurrentDictionary<string, int> unfixable = new(StringComparer.OrdinalIgnoreCase);
    int fixerFailures = 0;
    ProcessStartInfo psi = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in arguments) psi.ArgumentList.Add(a);

    void Handle(string? line)
    {
        if (line is null) return;
        if (line.Contains("Failed to apply code fix", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref fixerFailures);
        Match m = Regex.Match(line, @"Unable to fix (\S+?)\.");
        if (m.Success)
        {
            unfixable.AddOrUpdate(m.Groups[1].Value, 1, (_, count) => count + 1);
            return; // swallow the raw line; summarized at the end
        }
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
    if (p.ExitCode != 0) return p.ExitCode;
    if (fixerFailures == 0) return 0;
    Console.Error.WriteLine($"{fixerFailures} code-fixer failure(s) were logged despite dotnet format exiting 0.");
    return 1;
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
int UsageError(string message) { Console.Error.WriteLine($"{message} Try --help."); return 2; }

void PrintHelp() => Console.WriteLine(
"""
csharp-style — C# cleanup (code fixes, member ordering, whitespace) + scaffolder

USAGE
  csharp-style init [--solution <f>] [--force]
  csharp-style run  [--all] [--base <ref>] [--list] [--no-reorder]
                    [--exclude <glob>]... [--solution <f>]

  (run is the default command, so `csharp-style --all` works too.)
  Unknown arguments, missing values, and options for another command exit 2
  before making changes. Use --help or -h to display this help.

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
