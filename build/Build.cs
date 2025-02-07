using Invariants;
using Nuke.Common;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Serilog;
using System;
using System.Linq;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Push);

    const string BinPattern = "**/bin";
    const string ObjPattern = "**/obj";


    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;


    [Solution(GenerateProjects = true)]
    readonly Solution Solution;

    [Parameter]
    readonly bool IgnoreFailedSources;

    [Parameter]
    readonly string ReleaseNotes;

    [GitVersion(NoFetch = false)]
    readonly GitVersion GitVersion;

    [Parameter]
    readonly string NugetApiUrl;

    [Parameter]
    [Secret]
    readonly string NugetApiKey;

    [Parameter]
    readonly string NugetOrgApiUrl;

    [Parameter]
    [Secret]
    readonly string NugetOrgApiKey;

    static AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    static AbsolutePath SourceDirectory => RootDirectory / "src" / ProjectValues.BusinessValidationProject;
    static AbsolutePath MainLibraryDirectory => SourceDirectory / ProjectValues.BusinessValidationProject;
    static AbsolutePath TestsDirectory => SourceDirectory / "BusinessValidation.Tests";


    Target Print => _ => _
    .Description(Descriptions.Print)
    .DependentFor(Clean)
    .Executes(() =>
    {
        Log.Information("Release Notes = {Value}", ReleaseNotes);
        Log.Information("Root Directory = {Value}", RootDirectory);        
        Log.Information("Major Minor Patch = {Value}", GitVersion.MajorMinorPatch);
        Log.Information("NuGet Version = {Value}", GitVersion.NuGetVersion);
        Log.Information("PreReleaseLabel = {Value}", GitVersion?.PreReleaseLabel ?? "????");
    });

    Target Clean => _ => _
        .Unlisted()
        .Description(Descriptions.Clean)
        .Executes(() =>
        {
            MainLibraryDirectory.GlobDirectories(BinPattern, ObjPattern).ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories(BinPattern, ObjPattern).ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
    .Description(Descriptions.Restore)
    .DependsOn(Clean)
    .Executes(() =>
        {
            //Log.Information("IgnoreFailedSources: {IgnoreFailedSources}", IgnoreFailedSources);
            DotNetRestore(_ => _
            .SetProjectFile(Solution)
            .SetIgnoreFailedSources(IgnoreFailedSources)
            );
        });

    Target Compile => _ => _
        .Description(Descriptions.Compile)
        .DependsOn(Restore)
        .Executes(() =>
        {
            var publishConfiguration =
                from project in new[] { Solution.BusinessValidation, Solution.BusinessValidation_Tests }
                from framework in project.GetTargetFrameworks()
                select (project, framework);

            DotNetPublish(_ =>
                _.SetProject(Solution)
                .SetConfiguration(Configuration)
                .SetCopyright(PackageValues.Copyright)
                .SetAssemblyVersion(GitVersion.AssemblySemFileVer)
                .SetFileVersion(GitVersion.MajorMinorPatch)
                .SetVersion(GitVersion.NuGetVersion)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .CombineWith(publishConfiguration, (_, v) =>
                    _.SetProject(v.project)
                    .SetFramework(v.framework))
                );
        });

    Target Test => _ => _
    .Description(Descriptions.Test)
    .DependsOn(Compile)
    .Executes(() =>
    {
        DotNetTest(_ => _
        .SetProjectFile(Solution.BusinessValidation_Tests)
        .SetConfiguration(Configuration)
        .EnableNoBuild()
        );
    });

    Target Pack => _ => _
    .Description(Descriptions.Pack)
    .DependsOn(Test)
    .Executes(() => DotNetPack(s => s
            .SetProject(Solution.BusinessValidation)
            .SetConfiguration(Configuration)
            .EnableNoBuild()
            .EnableNoRestore()
            .SetNoDependencies(true)
            .SetPackageId(ProjectValues.BusinessValidationProject)
            .SetTitle(ProjectValues.BusinessValidationProject)
            .SetVersion(GitVersion.NuGetVersion)
            .SetRepositoryType(AzurePipelinesRepositoryType.Git.ToString().ToLowerInvariant())
            .SetPackageReleaseNotes(ReleaseNotes)
            .SetPackageProjectUrl(PackageValues.ProjectUrl)
            .SetAuthors(PackageValues.Author)
            .SetProperty(PackageProperties.PackageLicenseExpression, PackageValues.MITLicence)
            .SetPackageTags(PackageValues.Tags)
            .SetPackageRequireLicenseAcceptance(false)
            .SetDescription(PackageValues.Description)
            .SetRepositoryUrl(PackageValues.RepositoryUrl)
            .SetProperty(PackageProperties.RepositoryBranch, GitVersion.BranchName)
            .SetProperty(PackageProperties.RepositoryCommit, GitVersion.Sha)
            .SetProperty(PackageProperties.Copyright, PackageValues.Copyright)
            .SetProperty(PackageProperties.PackageReadmeFile, PackageValues.Readme)
            .SetProperty(PackageProperties.PackageIcon, PackageValues.Icon)
            .SetOutputDirectory(ArtifactsDirectory)
        ));

    Target Push => _ => _
        .Description(Descriptions.Push)
        .OnlyWhenStatic(() => IsServerBuild) // checked before the build steps run.
        .Requires(() => NugetApiKey)
        .Requires(() => NugetApiUrl)
        .Requires(() => Configuration.Equals(Configuration.Release))
        .DependsOn(Pack)
        .Executes(() =>
        {
            var nugetFiles = GlobFiles(ArtifactsDirectory, "*.nupkg");

            Assert.NotEmpty(nugetFiles, "There are no Nuget files");

            var branchName = GitVersion.BranchName;            

            // if we are on the main branch and it is not a pre-release, publish to Nuget.org
            if(branchName.Equals("main", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(GitVersion.PreReleaseLabel))
            {
                nugetFiles.Where(x => !x.EndsWith("symbols.nupkg"))
                    .ForEach(x =>
                    {
                        DotNetNuGetPush(s => s
                            .SetTargetPath(x)
                            .SetSource(NugetOrgApiUrl)
                            .SetApiKey(NugetOrgApiKey)
                        );
                    });
            }

            nugetFiles.Where(x => !x.EndsWith("symbols.nupkg"))
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(NugetApiUrl)
                        .SetApiKey(NugetApiKey)
                    );
                });
        });
}
