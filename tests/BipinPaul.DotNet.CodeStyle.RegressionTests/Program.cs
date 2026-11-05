using System.Text.Json;
using BipinPaul.DotNet.CodeStyle.RegressionTests;
using static BipinPaul.DotNet.CodeStyle.RegressionTests.HarnessEnvironment;

if (Environment.GetEnvironmentVariable(FakeDotnet.ModeVariable) == "1")
{
    return await FakeDotnet.RunAsync(args);
}

try
{
    string? dotnet = null;
    string? tool = null;
    bool realTools = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--dotnet" when i + 1 < args.Length:
                dotnet = args[++i];
                break;
            case "--tool" when i + 1 < args.Length:
                tool = args[++i];
                break;
            case "--real-tools":
                realTools = true;
                break;
            default:
                throw new ArgumentException("Usage: RegressionTests --dotnet <absolute host> --tool <absolute tool DLL>");
        }
    }
    Require(dotnet is not null && tool is not null,
        "Usage: RegressionTests --dotnet <absolute host> --tool <absolute tool DLL>");
    using HarnessEnvironment environment = new(dotnet!, tool!);
    return await RegressionCases.RunAsync(environment, realTools);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

internal static class RegressionCases
{
    private const string BeginMarker = "# >>> csharp-style (managed — re-init overwrites this block) >>>";
    private const string EndMarker = "# <<< csharp-style (managed) <<<";

    internal static async Task<int> RunAsync(HarnessEnvironment environment, bool realTools)
    {
        List<(string Name, Func<HarnessEnvironment, Task> Run)> cases =
        [
            ("unknown arguments fail before any repository changes", UnknownArgumentsAsync),
            ("missing option values fail before any repository changes", MissingValuesAsync),
            ("options for another command fail before any repository changes", UnsupportedOptionsAsync),
            ("help starts no child commands and leaves files unchanged", HelpArgumentsAsync),
            ("format alias and short options retain the cleanup pipeline", FormatAliasAsync),
            ("base reference options include committed changes", BaseReferenceAsync),
            ("pinned install uses the declared version and manifest", PinnedInstallAsync),
            ("restore fallback retains the same pinned manifest", RestoreFallbackAsync),
            ("missing manifest fails before source changes", MissingManifestAsync),
            ("manifest without a ReSharper pin fails before source changes", MissingPinAsync),
            ("list mode starts no child dotnet commands", ListAsync),
            ("empty changed scope starts no child dotnet commands", EmptyScopeAsync),
            ("changed scope is captured once for all stages", ChangedScopeAsync),
            ("whole-solution exclusions reach all stages", WholeScopeAsync),
            ("no-reorder skips setup and cleanup", NoReorderAsync),
            ("setup failure propagates and stops", SetupFailureAsync),
            ("analyzer failure propagates and stops", AnalyzerFailureAsync),
            ("reorder failure propagates and stops", ReorderFailureAsync),
            ("whitespace failure propagates", WhitespaceFailureAsync),
            ("stdout code-fixer exception fails despite child exit zero", FixerStdoutAsync),
            ("stderr code-fixer exception fails despite child exit zero", FixerStderrAsync),
            ("concurrent diagnostics retain code-fixer failure", ConcurrentDiagnosticsAsync),
            ("manual diagnostics remain informational", ManualDiagnosticsAsync),
            ("fresh init is byte-identical on its second run", InitIdempotentAsync),
            ("init preserves an unmanaged EditorConfig byte-for-byte", InitPreservesUnmanagedAsync),
            ("init force replaces an unmanaged EditorConfig", InitForceAsync),
            ("malformed managed markers fail without another block", MalformedMarkersAsync),
            ("generated EditorConfig severity values are valid exact tokens", SeverityTokensAsync)
        ];
        cases.AddRange(DotSettingsCases.Cases);
        cases.AddRange(RazorCases.Cases);
        if (realTools)
        {
            cases.Add(("real pinned cleanup preserves runtime behavior and is idempotent", IntegrationCases.OrderingAsync));
            cases.Add(("real Razor components compile before and after idempotent cleanup", IntegrationCases.RazorAsync));
        }

        int failures = 0;
        foreach ((string name, Func<HarnessEnvironment, Task> run) in cases)
        {
            try
            {
                await run(environment);
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
            }
        }
        Console.WriteLine($"{cases.Count - failures} passed; {failures} failed; {cases.Count} total.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task UnknownArgumentsAsync(HarnessEnvironment environment)
    {
        await RejectArgumentsAsync(environment,
        [
            ["--version"],
            ["run", "--all", "--al"],
            ["format", "--typo"],
            ["init", "--typo"],
            ["help", "--typo"],
            ["no-such-command"],
            ["run", "unexpected"],
            ["init", "unexpected"],
            ["--all", "run"],
            ["--help", "--typo"],
            ["--typo", "--help"]
        ]);
    }

    private static async Task MissingValuesAsync(HarnessEnvironment environment)
    {
        List<string[]> arguments = [];
        foreach (string option in new[] { "--base", "-b", "--solution", "-s", "--exclude", "-e" })
        {
            arguments.Add(["run", option]);
            arguments.Add(["run", option, ""]);
            arguments.Add(["run", option, " "]);
            arguments.Add(["run", option, "--all"]);
        }
        arguments.Add(["init", "--solution"]);
        arguments.Add(["init", "--solution", "--help"]);
        await RejectArgumentsAsync(environment, arguments);
    }

    private static async Task UnsupportedOptionsAsync(HarnessEnvironment environment)
    {
        await RejectArgumentsAsync(environment,
        [
            ["init", "--all"],
            ["init", "-a"],
            ["init", "--base", "main"],
            ["init", "--exclude", "src/**"],
            ["init", "--list"],
            ["init", "--no-reorder"],
            ["run", "--force"],
            ["format", "-f"],
            ["help", "--solution", "Sample.slnx"]
        ]);
    }

    private static async Task RejectArgumentsAsync(HarnessEnvironment environment, IEnumerable<string[]> argumentSets)
    {
        foreach (string[] arguments in argumentSets)
        {
            using RegressionFixture fixture = environment.CreateFixture();
            await fixture.CommitBaselineAsync();
            fixture.Write(RegressionFixture.SourceRelative, "internal sealed class Sample { int Changed; }\n");
            Dictionary<string, byte[]> before = fixture.Snapshot();
            ProcessResult result = await fixture.RunAsync(new(), arguments);
            string invocation = JsonSerializer.Serialize(arguments);
            Require(result.ExitCode == 2, $"Invalid invocation {invocation} should exit 2, got {result.ExitCode}: {result.Output}");
            Require(result.StandardError.Contains("--help", StringComparison.Ordinal),
                "Invalid invocation did not explain how to get usage help: " + invocation);
            Require(fixture.Calls.Count == 0, "Invalid invocation started child dotnet commands: " + invocation);
            SameSnapshot(before, fixture.Snapshot());
        }
    }

    private static async Task HelpArgumentsAsync(HarnessEnvironment environment)
    {
        string[][] argumentSets = [["--help"], ["-h"], ["help"], ["run", "--help"], ["init", "-h"], ["format", "--help"]];
        foreach (string[] arguments in argumentSets)
        {
            using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
            Dictionary<string, byte[]> before = fixture.Snapshot();
            ProcessResult result = await fixture.RunAsync(new(), arguments);
            Success(result);
            Require(result.StandardOutput.Contains("USAGE", StringComparison.Ordinal), "Help did not print usage.");
            Require(fixture.Calls.Count == 0, "Help started child dotnet commands.");
            SameSnapshot(before, fixture.Snapshot());
        }
    }

    private static async Task FormatAliasAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        ProcessResult result = await fixture.RunAsync(new(), "format", "-a", "-s", "Sample.slnx", "-e", RegressionFixture.ExcludedRelative);
        Success(result);
        Workflow(fixture, "analyzers", "reorder", "whitespace");
        foreach (string[] call in FormattingCalls(fixture))
        {
            Require(call.Contains("Sample.slnx"), "Format alias lost its explicit solution.");
            Equal([RegressionFixture.ExcludedRelative], OptionValues(call, "--exclude"), "A short option lost its exclusion.");
        }
    }

    private static async Task BaseReferenceAsync(HarnessEnvironment environment)
    {
        foreach (string option in new[] { "--base", "-b" })
        {
            using RegressionFixture fixture = environment.CreateFixture();
            await fixture.CommitBaselineAsync();
            fixture.Write(RegressionFixture.SourceRelative, "internal sealed class Sample { int CommittedChange; }\n");
            await fixture.CommitBaselineAsync();
            ProcessResult result = await fixture.RunAsync(new(), "run", option, "HEAD~1");
            Success(result);
            Workflow(fixture, "analyzers", "reorder", "whitespace");
            foreach (string[] call in FormattingCalls(fixture))
            {
                Equal([RegressionFixture.SourceRelative], OptionValues(call, "--include"),
                    "The base reference option omitted a committed change: " + option);
            }
        }
    }

    private static async Task PinnedInstallAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        string manifestPath = Path.Combine(fixture.Root, ".config/dotnet-tools.json");
        byte[] before = await File.ReadAllBytesAsync(manifestPath);
        ProcessResult result = await fixture.RunAsync(new(), "run", "--all");
        Success(result);
        string[] install = fixture.Calls.Single(call => FakeDotnet.Stage(call) == "install");
        Equal([RegressionFixture.Pin], OptionValues(install, "--version"), "The install must explicitly use the declared pin.");
        string[] manifests = OptionValues(install, "--tool-manifest");
        Require(manifests.Length == 1 && Path.GetFullPath(manifests[0]) == Path.GetFullPath(manifestPath),
            "The install must explicitly target the fixture manifest.");
        Require(OptionValues(install, "--arch").Length == 1, "The architecture-specific installation argument was lost.");
        byte[] after = await File.ReadAllBytesAsync(manifestPath);
        Require(before.SequenceEqual(after), "Run rewrote the pinned manifest.");
        Workflow(fixture, "analyzers", "reorder", "whitespace");
        Require(fixture.Calls.Single(call => FakeDotnet.Stage(call) == "reorder").Contains("--no-build"),
            "Reordering must be able to repair source that fails the preliminary build's ordering checks.");
    }

    private static Task MissingManifestAsync(HarnessEnvironment environment) => MissingSetupAsync(environment, missingManifest: true);

    private static async Task RestoreFallbackAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        ProcessResult result = await fixture.RunAsync(new() { InstallExit = 30 }, "run", "--all");
        Success(result);
        string[] restore = fixture.Calls.Single(call => FakeDotnet.Stage(call) == "restore");
        string[] manifests = OptionValues(restore, "--tool-manifest");
        Require(manifests.Length == 1 && Path.GetFullPath(manifests[0]) ==
            Path.GetFullPath(Path.Combine(fixture.Root, ".config/dotnet-tools.json")),
            "Restore fallback did not explicitly use the same pinned manifest.");
        Workflow(fixture, "analyzers", "reorder", "whitespace");
    }

    private static Task MissingPinAsync(HarnessEnvironment environment) => MissingSetupAsync(environment, missingManifest: false);

    private static async Task MissingSetupAsync(HarnessEnvironment environment, bool missingManifest)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        string manifestPath = Path.Combine(fixture.Root, ".config/dotnet-tools.json");
        if (missingManifest)
        {
            File.Delete(manifestPath);
        }
        else
        {
            fixture.Write(".config/dotnet-tools.json", "{\"version\":1,\"isRoot\":true,\"tools\":{}}\n");
        }
        Dictionary<string, byte[]> before = fixture.Snapshot();
        ProcessResult result = await fixture.RunAsync(new(), "run", "--all");
        Require(result.ExitCode != 0, "A missing pin unexpectedly succeeded.");
        Require(fixture.Calls.Count == 0, "Missing setup launched child dotnet commands.");
        SameSnapshot(before, fixture.Snapshot());
    }

    private static async Task ListAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        await fixture.CommitBaselineAsync();
        fixture.Write(RegressionFixture.SourceRelative, "internal sealed class Sample { int Changed; }\n");
        ProcessResult changed = await fixture.RunAsync(new(), "run", "--list");
        Success(changed);
        Require(changed.Output.Contains(RegressionFixture.SourceRelative, StringComparison.Ordinal), "List omitted the changed file.");
        Require(fixture.Calls.Count == 0, "Changed-file list launched child dotnet commands.");
        ProcessResult whole = await fixture.RunAsync(new(), "run", "--all", "--list");
        Success(whole);
        Require(fixture.Calls.Count == 0, "Whole-solution list launched child dotnet commands.");
    }

    private static async Task EmptyScopeAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        await fixture.CommitBaselineAsync();
        ProcessResult result = await fixture.RunAsync(new(), "run");
        Success(result);
        Require(fixture.Calls.Count == 0, "An empty changed-file scope launched child dotnet commands.");
    }

    private static async Task ChangedScopeAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        await fixture.CommitBaselineAsync();
        fixture.Write(RegressionFixture.SourceRelative, "internal sealed class Sample { int Changed; }\n");
        fixture.Write(RegressionFixture.ExcludedRelative, "internal sealed class Excluded { int Changed; }\n");
        fixture.Write("outside/Other.cs", "internal sealed class Other { int Changed; }\n");
        ProcessResult result = await fixture.RunAsync(new() { CreateAdditionalSource = true },
            "run", "--exclude", RegressionFixture.ExcludedRelative);
        Success(result);
        Workflow(fixture, "analyzers", "reorder", "whitespace");
        foreach (string[] call in FormattingCalls(fixture))
        {
            Equal([RegressionFixture.SourceRelative], OptionValues(call, "--include"),
                "A stage lost the original changed-file scope or rediscovered analyzer-created files.");
        }
    }

    private static async Task WholeScopeAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        fixture.Write(".csharp-style.json", "{\"exclude\":[\"outside/**\"]}\n");
        ProcessResult result = await fixture.RunAsync(new(), "--all", "--exclude", RegressionFixture.ExcludedRelative);
        Success(result);
        Workflow(fixture, "analyzers", "reorder", "whitespace");
        foreach (string[] call in FormattingCalls(fixture))
        {
            Equal(["outside/**", RegressionFixture.ExcludedRelative], OptionValues(call, "--exclude"),
                "A stage lost configured or command-line exclusions.");
            Require(OptionValues(call, "--include").Length == 0, "Whole-solution mode unexpectedly narrowed its scope.");
        }
    }

    private static async Task NoReorderAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        ProcessResult result = await fixture.RunAsync(new(), "run", "--all", "--no-reorder");
        Success(result);
        Equal(["analyzers", "whitespace"], fixture.Calls.Select(FakeDotnet.Stage), "No-reorder still performed setup or cleanup.");
    }

    private static async Task SetupFailureAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        byte[] source = await File.ReadAllBytesAsync(fixture.SourcePath);
        ProcessResult result = await fixture.RunAsync(new() { InstallExit = 31, RestoreExit = 31 }, "run", "--all");
        Require(result.ExitCode == 31, "Setup failure exit code was not preserved: " + result.Output);
        Require(fixture.Calls.All(call => FakeDotnet.Stage(call) is "install" or "restore"), "Formatting ran after failed setup.");
        byte[] sourceAfter = await File.ReadAllBytesAsync(fixture.SourcePath);
        Require(source.SequenceEqual(sourceAfter), "Failed setup changed source.");
    }

    private static Task AnalyzerFailureAsync(HarnessEnvironment environment) =>
        StageFailureAsync(environment, new() { AnalyzerExit = 41 }, 41, "analyzers");

    private static Task ReorderFailureAsync(HarnessEnvironment environment) =>
        StageFailureAsync(environment, new() { ReorderExit = 42 }, 42, "analyzers", "reorder");

    private static Task WhitespaceFailureAsync(HarnessEnvironment environment) =>
        StageFailureAsync(environment, new() { WhitespaceExit = 43 }, 43, "analyzers", "reorder", "whitespace");

    private static async Task StageFailureAsync(HarnessEnvironment environment, FakeScenario scenario, int expectedExit, params string[] stages)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        ProcessResult result = await fixture.RunAsync(scenario, "run", "--all");
        Require(result.ExitCode == expectedExit, $"Expected exit {expectedExit}, got {result.ExitCode}: {result.Output}");
        Workflow(fixture, stages);
    }

    private static Task FixerStdoutAsync(HarnessEnvironment environment) =>
        FixerFailureAsync(environment, new() { FixerFailureStream = "stdout" });

    private static Task FixerStderrAsync(HarnessEnvironment environment) =>
        FixerFailureAsync(environment, new() { FixerFailureStream = "stderr" });

    private static Task ConcurrentDiagnosticsAsync(HarnessEnvironment environment) =>
        FixerFailureAsync(environment, new() { ConcurrentDiagnostics = true });

    private static async Task FixerFailureAsync(HarnessEnvironment environment, FakeScenario scenario)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        ProcessResult result = await fixture.RunAsync(scenario, "run", "--all");
        Require(result.ExitCode != 0, "A code-fixer exception with child exit zero was reported as success.");
        Require(result.Output.Contains("Failed to apply code fix", StringComparison.OrdinalIgnoreCase), "Code-fixer failure evidence was hidden.");
        Workflow(fixture, "analyzers");
    }

    private static async Task ManualDiagnosticsAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        ProcessResult result = await fixture.RunAsync(new() { ManualDiagnostics = true }, "run", "--all");
        Success(result);
        Require(result.Output.Contains("SA1204", StringComparison.Ordinal) && result.Output.Contains("IDE1006", StringComparison.Ordinal),
            "Manual diagnostics were discarded rather than summarized.");
        Workflow(fixture, "analyzers", "reorder", "whitespace");
    }

    private static async Task InitIdempotentAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        Success(await fixture.RunAsync(new(), "init"));
        Dictionary<string, byte[]> first = fixture.Snapshot();
        Success(await fixture.RunAsync(new(), "init"));
        SameSnapshot(first, fixture.Snapshot());
        Require(fixture.Calls.Count == 0, "Init unexpectedly launched child dotnet commands.");
    }

    private static async Task InitPreservesUnmanagedAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        fixture.Write(".editorconfig", "root = true\r\n\r\n[*.cs]\r\nindent_size = 2\r\n");
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".editorconfig"));
        Success(await fixture.RunAsync(new(), "init"));
        byte[] after = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".editorconfig"));
        Require(before.SequenceEqual(after),
            "Init modified or appended a baseline to an unmanaged EditorConfig.");
    }

    private static async Task InitForceAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        const string original = "root = true\n[*.cs]\nindent_size = 2\n";
        fixture.Write(".editorconfig", original);
        Success(await fixture.RunAsync(new(), "init", "--force"));
        string updated = await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".editorconfig"));
        Require(updated != original && updated.Contains(">>> csharp-style", StringComparison.Ordinal),
            "Force did not replace the unmanaged EditorConfig with the managed baseline.");
    }

    private static async Task MalformedMarkersAsync(HarnessEnvironment environment)
    {
        string[] malformed =
        [
            BeginMarker + "\n[*.cs]\nindent_size = 2\n",
            EndMarker + "\n",
            BeginMarker + "\n" + BeginMarker + "\n" + EndMarker + "\n",
            EndMarker + "\n" + BeginMarker + "\n"
        ];
        foreach (string contents in malformed)
        {
            using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
            fixture.Write(".editorconfig", contents);
            byte[] before = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".editorconfig"));
            ProcessResult result = await fixture.RunAsync(new(), "init");
            Require(result.ExitCode != 0, "Init accepted malformed managed markers.");
            byte[] after = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".editorconfig"));
            Require(before.SequenceEqual(after),
                "Init altered malformed markers or appended a duplicate managed block.");
        }
    }

    private static async Task SeverityTokensAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        Success(await fixture.RunAsync(new(), "init"));
        HashSet<string> valid = ["none", "silent", "suggestion", "warning", "error", "default"];
        int diagnostics = 0;
        int styleOptions = 0;
        foreach (string raw in await File.ReadAllLinesAsync(Path.Combine(fixture.Root, ".editorconfig")))
        {
            string line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith(';') || !line.Contains('='))
            {
                continue;
            }
            string[] assignment = line.Split('=', 2);
            string key = assignment[0].Trim();
            string value = assignment[1].Trim();
            if (key.EndsWith(".severity", StringComparison.Ordinal))
            {
                diagnostics++;
                Require(valid.Contains(value), "Invalid exact diagnostic severity value for " + key + ": " + value);
            }
            else if ((key.StartsWith("csharp_style_", StringComparison.Ordinal) || key.StartsWith("dotnet_style_", StringComparison.Ordinal) ||
                      key == "csharp_prefer_braces") && value.Contains(':'))
            {
                styleOptions++;
                string severity = value[(value.LastIndexOf(':') + 1)..].Trim();
                Require(valid.Contains(severity), "Invalid exact style-option severity value for " + key + ": " + severity);
            }
        }
        Require(diagnostics > 0 && styleOptions > 0, "Severity checks did not inspect generated diagnostic and style-option entries.");
    }

    private static IEnumerable<string[]> FormattingCalls(RegressionFixture fixture) =>
        fixture.Calls.Where(call => FakeDotnet.Stage(call) is "analyzers" or "reorder" or "whitespace");

    private static void Workflow(RegressionFixture fixture, params string[] expected) =>
        Equal(expected, FormattingCalls(fixture).Select(FakeDotnet.Stage), "Unexpected formatting stage sequence.");

    private static string[] OptionValues(IReadOnlyList<string> arguments, string option)
    {
        List<string> values = [];
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith(option + "=", StringComparison.Ordinal))
            {
                values.AddRange(arguments[i][(option.Length + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries));
            }
            else if (arguments[i] == option)
            {
                while (i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values.Add(arguments[++i]);
                }
            }
        }
        return values.Select(value => value.Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
    }

    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        string[] expectedArray = expected.ToArray();
        string[] actualArray = actual.ToArray();
        Require(expectedArray.SequenceEqual(actualArray, StringComparer.Ordinal), message +
            " Expected " + JsonSerializer.Serialize(expectedArray) + "; got " + JsonSerializer.Serialize(actualArray));
    }

    private static void SameSnapshot(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal), "Init changed the generated file set.");
        foreach ((string path, byte[] bytes) in expected)
        {
            Require(bytes.SequenceEqual(actual[path]), "Unexpected byte changes in " + path);
        }
    }

    private static void Success(ProcessResult result) =>
        Require(result.ExitCode == 0, $"Expected success, got {result.ExitCode}: {result.Output}");
}
