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
            "Dad.AutoParty.Protocol.0.2.0-preview.3.nupkg",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "398dc012459d67507cdc652ae27ed290f4495722c09ed9814dea0841097b8510",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore \"${{ env.PROJECT_PATH }}\" -r win --locked-mode --source $packageDir --source $nugetSource",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore \"Tests\\dad.Tests.csproj\" --locked-mode --source $packageDir --source $nugetSource",
            workflow,
            StringComparison.Ordinal);
        var testLock = ReadRepositorySource("Tests", "packages.lock.json");
        Assert.Contains(
            "PMnocFJBrl5jDeqGAyNl1YLzf/gKcoxqZqnMmvGoMcrhKWneW/Ub/+qFzBQ5KK8Z67oz2L+bZlgR8FylHbj+wA==",
            testLock,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test \"Tests\\dad.Tests.csproj\" --configuration Release -p:Platform=x64 --no-restore",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("-p:DadDevPluginOutput=", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget add source", workflow, StringComparison.Ordinal);

        var project = ReadRepositorySource("dad.csproj");
        Assert.Contains(
            "<PluginRuntimeFiles Include=\"$(TargetDir)System.Formats.Cbor.dll\"",
            project,
            StringComparison.Ordinal);
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
