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
            "Dad.AutoParty.Protocol.0.2.0-preview.4.nupkg",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "644ea1dabcd0c3b2b371f58c05051561ee3d94ec6b56b357aea33b2d7c55967d",
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
            "rADFvaAvJ/5QAZ+ixQwgfQQrSjBRD0jCK30VEXvJ5vKxynLDfIYAqH9T47N8TeCQ+A7jj4s1LGKA21eX7yK2PQ==",
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
