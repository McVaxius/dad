using Xunit;

namespace dad.Tests;

public sealed class DadBuildReleaseWorkflowContractTests
{
    [Fact]
    public void RequiredReleaseTestsUseExplicitSourcesAndBlockArtifactUpload()
    {
        var workflow = ReadRepositorySource(".github", "workflows", "build-release.yml");
        var restore = workflow.IndexOf("- name: Restore", StringComparison.Ordinal);
        var test = workflow.IndexOf("- name: Test", StringComparison.Ordinal);
        var upload = workflow.IndexOf("- name: Upload Build Artifact", StringComparison.Ordinal);

        Assert.True(restore >= 0);
        Assert.True(test > restore);
        Assert.True(upload > test);
        Assert.Contains(
            "Dad.AutoParty.Protocol.0.1.0-preview.2.nupkg",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "475964fad1a400125b0a80a3ac4ab28e45150d5390d97e992fcf6dfb8dd09ac5",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore \"${{ env.PROJECT_PATH }}\" -r win --locked-mode --source $packageDir --source $nugetSource",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore \"Tests\\dad.Tests.csproj\" --source $packageDir --source $nugetSource",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test \"Tests\\dad.Tests.csproj\" --configuration Release -p:Platform=x64 --no-restore",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget add source", workflow, StringComparison.Ordinal);
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the Dad repository root from the test output directory.");
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }
}
