using System.Text.Json;

namespace BipinPaul.DotNet.CodeStyle.RegressionTests;

internal sealed class FakeScenario
{
    public int InstallExit { get; init; }
    public int RestoreExit { get; init; }
    public int AnalyzerExit { get; init; }
    public int ReorderExit { get; init; }
    public int WhitespaceExit { get; init; }
    public string? FixerFailureStream { get; init; }
    public bool ConcurrentDiagnostics { get; init; }
    public bool ManualDiagnostics { get; init; }
    public bool CreateAdditionalSource { get; init; }
}

internal static class FakeDotnet
{
    internal const string ModeVariable = "CODESTYLE_TEST_FAKE_DOTNET";

    internal static async Task<int> RunAsync(string[] arguments)
    {
        string log = RequiredEnvironment("CODESTYLE_TEST_LOG");
        await File.AppendAllTextAsync(log, JsonSerializer.Serialize(arguments) + "\n");
        FakeScenario scenario = JsonSerializer.Deserialize<FakeScenario>(
            await File.ReadAllTextAsync(RequiredEnvironment("CODESTYLE_TEST_SCENARIO")))!;
        string stage = Stage(arguments);
        switch (stage)
        {
            case "install":
                return scenario.InstallExit;
            case "restore":
                return scenario.RestoreExit;
            case "analyzers":
                if (scenario.AnalyzerExit != 0)
                {
                    return scenario.AnalyzerExit;
                }
                await File.AppendAllTextAsync(RequiredEnvironment("CODESTYLE_TEST_SOURCE"), "// analyzer pass\n");
                if (scenario.CreateAdditionalSource)
                {
                    await File.WriteAllTextAsync(RequiredEnvironment("CODESTYLE_TEST_NEW_SOURCE"),
                        "internal sealed class CreatedByAnalyzer { }\n");
                }
                const string failure = "Failed to apply code fix ExampleFixer for CA1861: simulated fixer exception.";
                if (scenario.ConcurrentDiagnostics)
                {
                    await Task.WhenAll(
                        Task.Run(() => EmitDiagnostics(Console.Out, failure)),
                        Task.Run(() => EmitDiagnostics(Console.Error, failure)));
                }
                else if (scenario.FixerFailureStream == "stderr")
                {
                    await Console.Error.WriteLineAsync(failure);
                }
                else if (scenario.FixerFailureStream == "stdout")
                {
                    await Console.Out.WriteLineAsync(failure);
                }
                if (scenario.ManualDiagnostics)
                {
                    await Console.Error.WriteLineAsync("Unable to fix SA1204. No associated code fix found.");
                    await Console.Out.WriteLineAsync("Unable to fix IDE1006. No associated code fix found.");
                }
                return 0;
            case "reorder":
                if (scenario.ReorderExit == 0)
                {
                    await File.AppendAllTextAsync(RequiredEnvironment("CODESTYLE_TEST_SOURCE"), "// reorder pass\n");
                }
                return scenario.ReorderExit;
            case "whitespace":
                return scenario.WhitespaceExit;
            default:
                await Console.Error.WriteLineAsync("Unexpected fake dotnet command: " + JsonSerializer.Serialize(arguments));
                return 64;
        }
    }

    internal static string Stage(IReadOnlyList<string> arguments)
    {
        if (arguments.Count >= 2 && arguments[0] == "tool" && arguments[1] == "install")
        {
            return "install";
        }
        if (arguments.Count >= 2 && arguments[0] == "tool" && arguments[1] == "restore")
        {
            return "restore";
        }
        if (arguments.Count > 0 && arguments[0] == "format")
        {
            return arguments.Count > 1 && arguments[1] == "whitespace" ? "whitespace" : "analyzers";
        }
        if (arguments.Contains("cleanupcode") &&
            (arguments[0] == "jb" || arguments.Count > 2 && arguments[0] == "tool" && arguments[1] == "run" && arguments[2] == "jb"))
        {
            return "reorder";
        }
        return "unknown";
    }

    private static void EmitDiagnostics(TextWriter writer, string failure)
    {
        for (int i = 0; i < 40; i++)
        {
            writer.WriteLine(i % 2 == 0 ? failure : "Unable to fix SA1204. No associated code fix found.");
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Missing test environment: " + name);
}
