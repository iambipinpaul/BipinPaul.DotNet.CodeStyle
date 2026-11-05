using System.Text;
using static BipinPaul.DotNet.CodeStyle.RegressionTests.HarnessEnvironment;

namespace BipinPaul.DotNet.CodeStyle.RegressionTests;

internal static class RazorCases
{
    private const string Imports = "@using Zeta;\n@using System.Net.Http;\n@using Alpha\n";
    private const string Sorted = "@using System.Net.Http\n@using Alpha\n@using Zeta\n";
    private const string RazorPath = "src/Sample/_Imports.razor";

    internal static IEnumerable<(string Name, Func<HarnessEnvironment, Task> Run)> Cases =>
    [
        ("Razor-only cleanup preserves encoding, markup and import membership", CleanupAsync),
        ("Razor list includes tracked and untracked files without mutation", ListAsync),
        ("Razor base selection includes committed changes", BaseAsync),
        ("whole-solution Razor cleanup respects exclusions and generated directories", WholeAsync),
        ("mixed changes keep Razor files out of C# child commands", MixedAsync),
        ("failed C# cleanup leaves Razor files unchanged", FailureAsync),
        ("unsupported Razor headers and comment contents remain untouched", BoundariesAsync)
    ];

    private static async Task CleanupAsync(HarnessEnvironment environment)
    {
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode })
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
                await fixture.CommitBaselineAsync();
                string header = "@page \"/sample\"\n@attribute [Route(RoutePath)]\n";
                string extras = "@using static Zeta.Helpers;\n@using static System.Math;\n@using Alias = Zeta.Value;\n";
                string sortedExtras = "@using static System.Math\n@using static Zeta.Helpers\n@using Alias = Zeta.Value\n";
                string body = "\n<div>@value</div>\n@code {\n    string value = \"\"\"\n@using Zeta\n@using Alpha\n\"\"\";\n}";
                string input = (header + Imports + extras + body).Replace("\n", newline);
                string expected = (header + Sorted + sortedExtras + body).Replace("\n", newline);
                string file = Path.Combine(fixture.Root, RazorPath);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, input, encoding);
                ProcessResult result = await fixture.RunAsync(new(), "run");
                Success(result);
                Require(fixture.Calls.Count == 0, "Razor-only changes invoked C# formatters or ReSharper setup.");
                byte[] expectedBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(expected)];
                byte[] actual = await File.ReadAllBytesAsync(file);
                Require(actual.SequenceEqual(expectedBytes), "Cleanup changed encoding, line endings, markup, or import membership.");
                Success(await fixture.RunAsync(new(), "run"));
                byte[] repeated = await File.ReadAllBytesAsync(file);
                Require(repeated.SequenceEqual(actual), "Repeated Razor cleanup was not idempotent.");
            }
    }

    private static async Task ListAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        fixture.Write(RazorPath, Sorted);
        await fixture.CommitBaselineAsync();
        fixture.Write(RazorPath, Imports);
        fixture.Write("src/Sample/New.razor", Imports);
        fixture.Write("src/Sample/Excluded.razor", Imports);
        fixture.Write("outside/Other.razor", Imports);
        fixture.Write(".csharp-style.json", "{\"exclude\":[\"**/Excluded.razor\"]}\n");
        Dictionary<string, byte[]> before = fixture.Snapshot();
        ProcessResult result = await fixture.RunAsync(new(), "run", "--list");
        Success(result);
        Require(result.Output.Contains(RazorPath, StringComparison.Ordinal)
            && result.Output.Contains("src/Sample/New.razor", StringComparison.Ordinal), "List omitted tracked or untracked Razor changes.");
        Require(!result.Output.Contains("Excluded.razor", StringComparison.Ordinal)
            && !result.Output.Contains("outside/Other.razor", StringComparison.Ordinal), "List included an excluded or out-of-solution file.");
        Require(fixture.Calls.Count == 0, "Razor list invoked a child formatter.");
        SameSnapshot(before, fixture.Snapshot());

        result = await fixture.RunAsync(new(), "run", "--all", "--list");
        Success(result);
        Require(result.Output.Contains(RazorPath, StringComparison.Ordinal), "Whole-solution list omitted Razor files.");
        SameSnapshot(before, fixture.Snapshot());
    }

    private static async Task BaseAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        await fixture.CommitBaselineAsync();
        fixture.Write(RazorPath, Imports);
        await fixture.CommitBaselineAsync();
        fixture.Write("src/Sample/Untracked.razor", Imports);
        Success(await fixture.RunAsync(new(), "run", "--base", "HEAD~1"));
        Require(await File.ReadAllTextAsync(Path.Combine(fixture.Root, RazorPath)) == Sorted, "Base selection missed a committed Razor change.");
        Require(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "src/Sample/Untracked.razor")) == Imports, "Base selection unexpectedly included an untracked file.");
        Require(fixture.Calls.Count == 0, "Razor base selection invoked C# formatters.");
    }

    private static async Task WholeAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        string[] excluded = ["src/Sample/Configured.razor", "src/Sample/Cli.razor", "src/Sample/bin/Generated.razor", "src/Sample/obj/Generated.razor", "outside/Other.razor"];
        fixture.Write(RazorPath, Imports);
        foreach (string file in excluded) fixture.Write(file, Imports);
        fixture.Write(".csharp-style.json", "{\"exclude\":[\"**/Configured.razor\"]}\n");
        Success(await fixture.RunAsync(new(), "run", "--all", "--exclude", "**/Cli.razor"));
        Require(await File.ReadAllTextAsync(Path.Combine(fixture.Root, RazorPath)) == Sorted, "Whole-solution cleanup missed Razor imports.");
        foreach (string file in excluded)
            Require(await File.ReadAllTextAsync(Path.Combine(fixture.Root, file)) == Imports, "Whole-solution cleanup modified an excluded file: " + file);
    }

    private static async Task MixedAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        await fixture.CommitBaselineAsync();
        fixture.Write(RegressionFixture.SourceRelative, "internal sealed class Sample { int Changed; }\n");
        fixture.Write(RazorPath, Imports);
        Success(await fixture.RunAsync(new(), "run"));
        foreach (string[] call in fixture.Calls.Where(call => FakeDotnet.Stage(call) is "analyzers" or "reorder" or "whitespace"))
        {
            string scope = string.Join(' ', call);
            Require(scope.Contains(RegressionFixture.SourceRelative, StringComparison.Ordinal), "C# cleanup lost its original scope.");
            Require(!scope.Contains(".razor", StringComparison.Ordinal), "A C# formatter received a Razor file.");
        }
        Require(await File.ReadAllTextAsync(Path.Combine(fixture.Root, RazorPath)) == Sorted, "Mixed cleanup omitted Razor imports.");
    }

    private static async Task FailureAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture();
        fixture.Write(RazorPath, Imports);
        ProcessResult result = await fixture.RunAsync(new() { AnalyzerExit = 41 }, "run", "--all");
        Require(result.ExitCode == 41, "C# failure exit code changed.");
        Require(await File.ReadAllTextAsync(Path.Combine(fixture.Root, RazorPath)) == Imports, "Razor cleanup ran after a failed C# stage.");
    }

    private static async Task BoundariesAsync(HarnessEnvironment environment)
    {
        string[] boundaries =
        [
            "<div>",
            "@code {",
            "@using (var item = Open()) {",
            "@attribute [Example(",
            "@attribute [Example(\"\"\"",
            "@using Alias = System.Collections.Generic.List<string>",
            "@using Alpha @* attached comment *@"
        ];
        foreach (string boundary in boundaries)
        {
            using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
            await fixture.CommitBaselineAsync();
            string original = "@* header comment\n@using Not.A.Directive;\n*@\n" + Imports
                + "\n" + boundary + "\n" + Imports;
            fixture.Write(RazorPath, original);
            Success(await fixture.RunAsync(new(), "run"));
            string expected = "@* header comment\n@using Not.A.Directive;\n*@\n" + Sorted
                + "\n" + boundary + "\n" + Imports;
            Require(await File.ReadAllTextAsync(Path.Combine(fixture.Root, RazorPath)) == expected,
                "Cleanup crossed a Razor syntax boundary: " + boundary);
        }
    }

    private static void Success(ProcessResult result) =>
        Require(result.ExitCode == 0, $"Expected success, got {result.ExitCode}: {result.Output}");

    private static void SameSnapshot(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Require(before.Count == after.Count && before.All(entry =>
            after.TryGetValue(entry.Key, out byte[]? content) && entry.Value.SequenceEqual(content)), "List mode modified repository files.");
    }
}
