using System.Linq;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Solutions;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;

class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Push);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath PackageOutputDirectory => ArtifactsDirectory / "Packages";

    AbsolutePath PackageProjectPath =>
        SourceDirectory / "BipinPaul.DotNet.CodeStyle.csproj";

    [Parameter("NuGet API Key for publishing the tool")] readonly string NuGetPAT;
    [Parameter("Package version (default: 1.0.9)")] readonly string PackageVersion = "1.0.9";

    #region NuGet

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(p => p.DeleteDirectory());
            ArtifactsDirectory.CreateOrCleanDirectory();
            PackageOutputDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetTasks.DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetBuild(s => s
                .SetProjectFile(PackageProjectPath)
                .SetConfiguration(Configuration)
                .SetProperty("Version", PackageVersion)
                .SetProperty("AssemblyVersion", PackageVersion)
                .EnableNoRestore());
        });

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetPack(s => s
                .SetConfiguration(Configuration.Release.ToString())
                .SetProject(PackageProjectPath)
                .SetVersion(PackageVersion)
                .SetOutputDirectory(PackageOutputDirectory)
                .EnableIncludeSymbols()
                .SetSymbolPackageFormat("snupkg"));
        });

    Target Push => _ => _
        .DependsOn(Pack)
        .Requires(() => NuGetPAT)
        .Executes(() =>
        {
            PackageOutputDirectory.GlobFiles("*.nupkg")
                .Where(x => !x.Name.EndsWith("symbols.nupkg"))
                .ForEach(x =>
                {
                    DotNetTasks.DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource("https://api.nuget.org/v3/index.json")
                        .SetApiKey(NuGetPAT)
                        .EnableSkipDuplicate());
                });
        });

    #endregion
}
