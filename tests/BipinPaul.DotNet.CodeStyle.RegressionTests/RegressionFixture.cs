using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace BipinPaul.DotNet.CodeStyle.RegressionTests;

internal sealed class HarnessEnvironment : IDisposable
{
    private readonly string _temporaryParent = Path.Combine(Path.GetTempPath(), "codestyle-regression");

    internal HarnessEnvironment(string dotnet, string tool)
    {
        Dotnet = RequireAbsoluteFile(dotnet, "--dotnet");
        Tool = RequireAbsoluteFile(tool, "--tool");
        Root = Path.Combine(_temporaryParent, Guid.NewGuid().ToString("N"));
        FakeHostDirectory = Path.Combine(Root, "fake-host");
        Directory.CreateDirectory(FakeHostDirectory);

        string assemblyName = Assembly.GetExecutingAssembly().GetName().Name!;
        string apphost = Path.Combine(AppContext.BaseDirectory, assemblyName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Require(File.Exists(apphost), "Regression apphost is missing. Build with UseAppHost=true before running the DLL.");
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(FakeHostDirectory, Path.GetFileName(file)), overwrite: true);
        }
        string fakeHost = Path.Combine(FakeHostDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.Copy(apphost, fakeHost, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeHost, File.GetUnixFileMode(apphost));
        }
        // .NET searches the parent executable's directory before PATH on Unix too.
        // Keep the tool apphost beside the fake host on every supported platform.
        string toolApphost = Path.Combine(Path.GetDirectoryName(Tool)!,
            Path.GetFileNameWithoutExtension(Tool) + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Require(File.Exists(toolApphost), "Tool apphost is missing beside --tool. Build a framework-dependent executable first.");
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(Tool)!))
        {
            File.Copy(file, Path.Combine(FakeHostDirectory, Path.GetFileName(file)), overwrite: true);
        }
        ToolApphost = Path.Combine(FakeHostDirectory, Path.GetFileName(toolApphost));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(ToolApphost, File.GetUnixFileMode(toolApphost));
    }

    internal string Dotnet { get; }
    internal string Tool { get; }
    internal string ToolApphost { get; }
    internal string Root { get; }
    internal string FakeHostDirectory { get; }

    internal RegressionFixture CreateFixture(bool styleConfiguration = true) => new(this, styleConfiguration);

    public void Dispose() => SafeDelete(Root, _temporaryParent);

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void SafeDelete(string path, string allowedParent)
    {
        string target = Path.GetFullPath(path);
        string parent = Path.GetFullPath(allowedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Require(target.StartsWith(parent, comparison), "Refusing to delete a path outside the test temporary directory.");
        if (Directory.Exists(target))
        {
            foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(target, recursive: true);
        }
    }

    internal static async Task<ProcessResult> ExecuteAsync(ProcessStartInfo start, TimeSpan? maximumDuration = null)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + start.FileName);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(maximumDuration ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Timed out running " + start.FileName);
        }
        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdout,
            StandardError = await stderr
        };
    }

    private static string RequireAbsoluteFile(string path, string option)
    {
        Require(Path.IsPathFullyQualified(path), option + " must be an absolute path.");
        Require(File.Exists(path), option + " does not exist: " + path);
        return Path.GetFullPath(path);
    }
}

internal sealed class ProcessResult
{
    internal required int ExitCode { get; init; }
    internal required string StandardOutput { get; init; }
    internal required string StandardError { get; init; }
    internal string Output => StandardOutput + StandardError;
}

internal sealed class RegressionFixture : IDisposable
{
    internal const string Pin = "2026.1.3";
    internal const string SourceRelative = "src/Sample/Sample.cs";
    internal const string ExcludedRelative = "src/Sample/Excluded.cs";
    private readonly HarnessEnvironment _environment;
    private readonly string _directory;
    private readonly string _control;
    private readonly string _log;

    internal RegressionFixture(HarnessEnvironment environment, bool styleConfiguration)
    {
        _environment = environment;
        _directory = Path.Combine(environment.Root, Guid.NewGuid().ToString("N"));
        Root = Path.Combine(_directory, "repo");
        _control = Path.Combine(_directory, "control");
        _log = Path.Combine(_control, "calls.jsonl");
        Directory.CreateDirectory(_control);
        Write("Sample.slnx", "<Solution><Project Path=\"src/Sample/Sample.csproj\" /></Solution>\n");
        Write("src/Sample/Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        Write(SourceRelative, "internal sealed class Sample { }\n");
        Write(ExcludedRelative, "internal sealed class Excluded { }\n");
        Write("outside/Other.cs", "internal sealed class Other { }\n");
        if (styleConfiguration)
        {
            Write(".config/dotnet-tools.json", "{\"version\":1,\"isRoot\":true,\"tools\":{\"jetbrains.resharper.globaltools\":{\"version\":\"" + Pin + "\",\"commands\":[\"jb\"]}}}\n");
            Write("Sample.slnx.DotSettings", "<wpf:ResourceDictionary xmlns:wpf=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:s=\"clr-namespace:System;assembly=mscorlib\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"></wpf:ResourceDictionary>\n");
        }
    }

    internal string Root { get; }
    internal string SourcePath => Path.Combine(Root, SourceRelative);
    internal IReadOnlyList<string[]> Calls => !File.Exists(_log) ? [] :
        File.ReadAllLines(_log).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();

    internal void Write(string relative, string text)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    internal async Task CommitBaselineAsync()
    {
        await GitAsync("init", "--quiet");
        await GitAsync("add", ".");
        await GitAsync("-c", "user.name=CodeStyle Regression", "-c", "user.email=codestyle-regression@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "--message", "fixture baseline");
    }

    internal async Task<ProcessResult> RunAsync(FakeScenario scenario, params string[] arguments)
    {
        string scenarioPath = Path.Combine(_control, "scenario.json");
        await File.WriteAllTextAsync(scenarioPath, JsonSerializer.Serialize(scenario));
        File.Delete(_log);
        ProcessStartInfo start = new(_environment.ToolApphost) { WorkingDirectory = Root };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["PATH"] = _environment.FakeHostDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment[FakeDotnet.ModeVariable] = "1";
        start.Environment["CODESTYLE_TEST_LOG"] = _log;
        start.Environment["CODESTYLE_TEST_SCENARIO"] = scenarioPath;
        start.Environment["CODESTYLE_TEST_SOURCE"] = SourcePath;
        start.Environment["CODESTYLE_TEST_NEW_SOURCE"] = Path.Combine(Root, "src/Sample/CreatedByAnalyzer.cs");
        string resolvedHost = File.ResolveLinkTarget(_environment.Dotnet, returnFinalTarget: true)?.FullName ?? _environment.Dotnet;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(resolvedHost)!;
        return await HarnessEnvironment.ExecuteAsync(start);
    }

    internal Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(Root, path).Replace('\\', '/').StartsWith(".git/", StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(Root, path), File.ReadAllBytes, StringComparer.Ordinal);

    public void Dispose() => HarnessEnvironment.SafeDelete(_directory, _environment.Root);

    private async Task GitAsync(params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = Root };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=" + Path.Combine(_control, "no-hooks"));
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.autocrlf=false");
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        ProcessResult result = await HarnessEnvironment.ExecuteAsync(start);
        HarnessEnvironment.Require(result.ExitCode == 0, "Fixture git command failed: " + result.Output);
    }
}
