using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadP1218AutoDutyContractTests
{
    [Fact]
    public void AutoRetainerRequestFaultNeverGrantsGlobalFinishAndTimesOutToRearm()
    {
        var lease = new DadAutoRetainerPostprocessLease();
        var now = DateTime.UtcNow;
        Assert.True(lease.Arm("operation-a", now).Accepted);
        var request = lease.BeginRequest(now);
        lease.MarkRequestFault(request.Generation);

        var cancel = lease.RequestFinish(retryAtNextBoundary: false, now.AddSeconds(1));

        Assert.True(cancel.Pending);
        Assert.False(cancel.ShouldFinish);
        Assert.True(lease.ExpirePending(now + DadAutoRetainerPostprocessLease.PendingRequestTimeout));
        Assert.Equal(DadAutoRetainerPostprocessLeaseStage.None, lease.Stage);
        Assert.True(lease.Arm("operation-b", now.AddMinutes(3)).Accepted);
    }

    [Fact]
    public void MissingAutoRetainerCallbackRearmsOnlyTheSameOperationAfterTimeout()
    {
        var lease = new DadAutoRetainerPostprocessLease();
        var now = DateTime.UtcNow;
        lease.Arm("operation-a", now);
        lease.BeginRequest(now);

        Assert.True(lease.ExpirePending(now + DadAutoRetainerPostprocessLease.PendingRequestTimeout));
        Assert.Equal(DadAutoRetainerPostprocessLeaseStage.Armed, lease.Stage);
        Assert.Equal("operation-a", lease.OperationToken);
        Assert.False(lease.Arm("operation-b", now.AddMinutes(3)).Accepted);
        Assert.True(lease.BeginRequest(now.AddMinutes(3)).ShouldRequest);
    }

    [Fact]
    public void AutoRetainerOwnedGenerationAloneCanFinish()
    {
        var lease = new DadAutoRetainerPostprocessLease();
        var now = DateTime.UtcNow;
        lease.Arm("operation-a", now);
        var request = lease.BeginRequest(now);
        var ready = lease.MarkReady(now.AddSeconds(1));
        var finish = lease.RequestFinish(retryAtNextBoundary: false, now.AddSeconds(2));

        Assert.Equal(request.Generation, ready.Generation);
        Assert.True(lease.IsOwned);
        Assert.True(finish.ShouldFinish);
        lease.FinishSucceeded(finish.Generation + 1, retryAtNextBoundary: false);
        Assert.True(lease.IsOwned);
        lease.FinishSucceeded(finish.Generation, retryAtNextBoundary: false);
        Assert.Equal(DadAutoRetainerPostprocessLeaseStage.None, lease.Stage);
    }

    [Fact]
    public void AutoRetainerDisposeDoesNotFinishPendingRequestButDoesFinishOwnedCallback()
    {
        var now = DateTime.UtcNow;
        var pending = new DadAutoRetainerPostprocessLease();
        pending.Arm("pending", now);
        pending.BeginRequest(now);

        var pendingDispose = pending.DisposeDecision();

        Assert.False(pendingDispose.ShouldFinish);
        Assert.Equal(DadAutoRetainerPostprocessLeaseStage.None, pending.Stage);

        var owned = new DadAutoRetainerPostprocessLease();
        owned.Arm("owned", now);
        owned.BeginRequest(now);
        owned.MarkReady(now.AddSeconds(1));

        Assert.True(owned.DisposeDecision().ShouldFinish);
    }

    [Fact]
    public void LateNamedDadCallbackAfterTimeoutIsOwnedAndImmediatelyReleased()
    {
        var lease = new DadAutoRetainerPostprocessLease();
        var now = DateTime.UtcNow;
        lease.Arm("operation", now);
        lease.BeginRequest(now);
        Assert.True(lease.ExpirePending(now + DadAutoRetainerPostprocessLease.PendingRequestTimeout));

        var late = lease.MarkReady(now.AddMinutes(3));

        Assert.True(late.ShouldFinish);
        Assert.True(lease.IsOwned);
        Assert.Equal("dad-ar-postprocess-stale-callback-owned", late.SafeCode);
    }

    [Fact]
    public void QuestionableRollbackRetainsPerFieldOwnershipUntilEveryRestoreSucceeds()
    {
        var source = ReadRepositorySource("Services", "DadQuestionableReflectionBridge.cs");
        var apply = Slice(source, "private void ApplyPatch", "private bool IsFullyOwned");
        var cosmeticApply = Slice(source, "private void ApplyCosmeticPatch", "private void ApplyPatch");
        var restore = Slice(source, "private void RestoreOwnedValues", "private void RestoreOwnedCosmeticValue");

        Assert.True(apply.IndexOf("ownership = newOwnership", StringComparison.Ordinal) <
                    apply.IndexOf("foreach (var subscriber in prepared.Subscribers)", StringComparison.Ordinal));
        Assert.Contains("RestoreOwnedValues();", apply, StringComparison.Ordinal);
        Assert.Contains("patch.Subscribers.ToList()", restore, StringComparison.Ordinal);
        Assert.Contains("patch.Subscribers.Remove(subscriber)", restore, StringComparison.Ordinal);
        Assert.Contains("patch.Subscribers.Count == 0 && !patch.DutyGateOwned", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("var patch = ownership;\n        ownership = null;", NormalizeLines(restore), StringComparison.Ordinal);
        Assert.True(cosmeticApply.IndexOf("cosmeticOwnership = new CosmeticPatchOwnership", StringComparison.Ordinal) <
                    cosmeticApply.IndexOf("RecommendedPluginsField.SetValue", StringComparison.Ordinal));
        Assert.Contains("RestoreOwnedCosmeticValue();", cosmeticApply, StringComparison.Ordinal);
    }

    [Fact]
    public void PerDadDiscordPilotAndFileCourierRuntimeAreRemoved()
    {
        Assert.False(File.Exists(RepositoryPath("Services", "DadMeasuredPilotService.cs")));
        Assert.False(File.Exists(RepositoryPath("Services", "DadAutoPartyFileCourierAdapter.cs")));
        Assert.False(File.Exists(RepositoryPath("Services", "DadAutoPartyPilotFixtureService.cs")));

        var project = ReadRepositorySource("dad.csproj");
        var plugin = ReadRepositorySource("Plugin.cs");
        var window = ReadRepositorySource("Windows", "DadAutoPartyWindow.cs");
        Assert.DoesNotContain("Discord.Net.WebSocket", project, StringComparison.Ordinal);
        Assert.DoesNotContain("BouncyCastle.Cryptography.PowerShell51.dll", project, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerShell51Verifier", project, StringComparison.Ordinal);
        Assert.DoesNotContain("\\lib\\net461\\BouncyCastle.Cryptography.dll", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<PackageReference Include=\"BouncyCastle.Cryptography\"", project, StringComparison.Ordinal);
        Assert.Contains("<PluginRuntimeFiles Include=\"$(TargetDir)BouncyCastle.Cryptography.dll\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("DadMeasuredPilotService", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("DadAutoPartyDiscordService", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("DadAutoPartyFileCourierAdapter", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("Start measured pilot", window, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnexpectedBackgroundFaultsAreWarningLevelAndRateLimited()
    {
        var source = ReadRepositorySource("Services", "DadBackgroundTaskObserver.cs");

        Assert.Contains("warningGate.ShouldEmit", source, StringComparison.Ordinal);
        Assert.Contains("log.Warning(", source, StringComparison.Ordinal);
        Assert.Contains("{Component} task '{Operation}' ended with an unexpected error", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedKillChoicesAreAbsentWhileLegacyEnumsRemain()
    {
        var configWindow = ReadRepositorySource("Windows", "ConfigWindow.cs");
        var mainWindow = ReadRepositorySource("Windows", "MainWindow.cs");
        var runner = ReadRepositorySource("Services", "DadCompletionActionRunner.cs");

        Assert.DoesNotContain("CompletionKillModes", configWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletionKillModes", mainWindow, StringComparison.Ordinal);
        Assert.Contains("legacy-kill-action-noop", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", runner, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string NormalizeLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the Dad repository root from the test output directory.");
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }

    private static string RepositoryPath(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the Dad repository root from the test output directory.");
        return Path.Combine([repositoryRoot, .. pathParts]);
    }
}
