using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using static BipinPaul.DotNet.CodeStyle.RegressionTests.HarnessEnvironment;

namespace BipinPaul.DotNet.CodeStyle.RegressionTests;

internal static class IntegrationCases
{
    internal static async Task OrderingAsync(HarnessEnvironment environment)
    {
        string sourceDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ordering");
        Require(Directory.Exists(sourceDirectory), "The real-tool ordering fixtures were not copied to the build output.");
        string directory = Path.Combine(environment.Root, "real-ordering");
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));

        await File.WriteAllTextAsync(Path.Combine(directory, "OrderingCases.slnx"),
            "<Solution><Project Path=\"OrderingCases.csproj\" /></Solution>\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"),
            "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestMinor\",\"allowPrerelease\":false}}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "OrderingCases.slnx.DotSettings"),
            ReadTemplate(environment.Tool, "solution.DotSettings.template"));
        string manifestDirectory = Path.Combine(directory, ".config");
        Directory.CreateDirectory(manifestDirectory);
        string manifestPath = Path.Combine(manifestDirectory, "dotnet-tools.json");
        await File.WriteAllTextAsync(manifestPath, ReadTemplate(environment.Tool, "dotnet-tools.json.template"));
        byte[] manifestBefore = await File.ReadAllBytesAsync(manifestPath);
        string sourcePath = Path.Combine(directory, "OrderingCases.cs");
        string originalSource = await File.ReadAllTextAsync(sourcePath);

        await RunRealAsync(environment, directory, environment.Tool, "run", "--all");
        string formattedSource = await File.ReadAllTextAsync(sourcePath);
        foreach (string region in new[] { "Preserved struct fields", "Preserved record struct fields", "Physical layout", "Ordered external side effects" })
            Require(RegionContents(originalSource, region) == RegionContents(formattedSource, region),
                "Cleanup removed or reordered a protected region: " + region);
        int regexCandidate = formattedSource.IndexOf("class RegexGenerationCandidate", StringComparison.Ordinal);
        Require(regexCandidate >= 0 && formattedSource[regexCandidate..].Contains("[GeneratedRegex(", StringComparison.Ordinal),
            "The integration test did not exercise generation of a new regex member before final ordering.");
        byte[] manifestAfter = await File.ReadAllBytesAsync(manifestPath);
        Require(manifestBefore.SequenceEqual(manifestAfter), "The real cleanup rewrote its pinned tool manifest.");
        await RunRealAsync(environment, directory, "build", "OrderingCases.slnx", "--no-restore", "--nologo", "-v", "minimal");
        ProcessResult executable = await RunRealAsync(environment, directory,
            Path.Combine(directory, "bin", "Debug", "net10.0", "OrderingCases.dll"));
        Require(executable.StandardOutput.Trim() == "Ordering fixture invariants passed.",
            "Member reordering changed a checked runtime invariant: " + executable.Output);

        byte[] firstCleanup = await File.ReadAllBytesAsync(sourcePath);
        await RunRealAsync(environment, directory, environment.Tool, "run", "--all");
        byte[] secondCleanup = await File.ReadAllBytesAsync(sourcePath);
        Require(firstCleanup.SequenceEqual(secondCleanup), "A second real cleanup changed the already formatted source.");
        manifestAfter = await File.ReadAllBytesAsync(manifestPath);
        Require(manifestBefore.SequenceEqual(manifestAfter), "A repeated cleanup rewrote its pinned tool manifest.");
    }

    private static string RegionContents(string source, string name)
    {
        int start = source.IndexOf("#region " + name, StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf("#endregion", start, StringComparison.Ordinal);
        Require(start >= 0 && end >= start, "Missing protected fixture region: " + name);
        // Final whitespace formatting is allowed; declaration order and regions are not.
        return Regex.Replace(source[start..(end + "#endregion".Length)], @"\s+", "");
    }

    private static async Task<ProcessResult> RunRealAsync(HarnessEnvironment environment, string directory,
        params string[] arguments)
    {
        ProcessStartInfo start = new(environment.Dotnet) { WorkingDirectory = directory };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove(FakeDotnet.ModeVariable);
        ProcessResult result = await ExecuteAsync(start, TimeSpan.FromMinutes(3));
        Require(result.ExitCode == 0,
            $"Real command {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.Output}");
        return result;
    }

    private static string ReadTemplate(string tool, string name)
    {
        Assembly assembly = Assembly.LoadFrom(tool);
        string resource = assembly.GetManifestResourceNames().Single(path => path.EndsWith("." + name, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
