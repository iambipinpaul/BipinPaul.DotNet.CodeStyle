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

    internal static async Task RazorAsync(HarnessEnvironment environment)
    {
        string directory = Path.Combine(environment.Root, "real-razor");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"), "<Project />\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"),
            "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestMinor\",\"allowPrerelease\":false}}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "RazorSample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup></Project>\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "RazorSample.slnx"),
            "<Solution><Project Path=\"RazorSample.csproj\" /></Solution>\n");
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitignore"), "bin/\nobj/\n");
        string importsPath = Path.Combine(directory, "_Imports.razor");
        await File.WriteAllTextAsync(importsPath,
            "@using Microsoft.AspNetCore.Components;\n@using System.Text;\n@using System.Net.Http;\n");
        string componentPath = Path.Combine(directory, "Sample.razor");
        string component = "@attribute [System.Obsolete]\n@using System.Text;\n@using System.Net.Http;\n\n"
            + "<p>@Text</p>\n@code {\n    string Text => new StringBuilder(\"preserved\").ToString();\n}\n";
        await File.WriteAllTextAsync(componentPath, component);
        ProcessStartInfo git = new("git") { WorkingDirectory = directory };
        git.ArgumentList.Add("init");
        Require((await ExecuteAsync(git)).ExitCode == 0, "Could not initialize the Razor fixture.");
        await RunRealAsync(environment, directory, "build", "--nologo", "-v", "minimal");
        await RunRealAsync(environment, directory, environment.Tool, "run");
        Require(await File.ReadAllTextAsync(importsPath) ==
            "@using System.Net.Http\n@using System.Text\n@using Microsoft.AspNetCore.Components\n",
            "The real Razor imports were not cleaned.");
        Require(await File.ReadAllTextAsync(componentPath) ==
            component.Replace("@using System.Text;\n@using System.Net.Http;", "@using System.Net.Http\n@using System.Text"),
            "The real Razor component changed outside its imports.");
        await RunRealAsync(environment, directory, "build", "--no-restore", "--nologo", "-v", "minimal");
        byte[] firstImports = await File.ReadAllBytesAsync(importsPath);
        byte[] firstComponent = await File.ReadAllBytesAsync(componentPath);
        await RunRealAsync(environment, directory, environment.Tool, "run");
        byte[] secondImports = await File.ReadAllBytesAsync(importsPath);
        byte[] secondComponent = await File.ReadAllBytesAsync(componentPath);
        Require(firstImports.SequenceEqual(secondImports)
            && firstComponent.SequenceEqual(secondComponent),
            "Real Razor cleanup was not idempotent.");
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
