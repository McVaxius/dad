using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLifecycleHardeningDutyTests
{
    [Fact]
    public void FreshDutyCompletedEvidenceWinsOverSameTickExit()
    {
        var completed = DadDutyLifecycleRules.ObserveDutyCompleted(
            enteredDuty: true,
            alreadyCompleted: false,
            freshCompletionEvidence: true);

        Assert.True(completed);
        Assert.True(DadDutyLifecycleRules.IsCompletedExit(true, completed, true));
        Assert.False(DadDutyLifecycleRules.IsAbandonedExit(true, completed, true));
    }

    [Fact]
    public void ExitWithoutCompletionEvidenceStartsBoundedGrace()
    {
        var completed = DadDutyLifecycleRules.ObserveDutyCompleted(
            enteredDuty: true,
            alreadyCompleted: false,
            freshCompletionEvidence: false);

        Assert.False(completed);
        var now = DateTime.UtcNow;
        var decision = DadDutyLifecycleRules.EvaluateExit(
            true,
            completed,
            true,
            DateTime.MinValue,
            now,
            TimeSpan.FromSeconds(10));

        Assert.Equal(DadDutyExitDisposition.WaitingForCompletion, decision.Disposition);
        Assert.Equal(now.AddSeconds(10), decision.GraceDeadlineUtc);
    }

    [Fact]
    public void DelayedCompletionWithinExitGraceCompletesTheExit()
    {
        var deadline = new DateTime(2026, 8, 2, 12, 0, 10, DateTimeKind.Utc);
        var completed = DadDutyLifecycleRules.ObserveDutyCompleted(
            enteredDuty: true,
            alreadyCompleted: false,
            freshCompletionEvidence: true);

        var decision = DadDutyLifecycleRules.EvaluateExit(
            true, completed, true, deadline, deadline.AddTicks(-1), TimeSpan.FromSeconds(10));

        Assert.Equal(DadDutyExitDisposition.Completed, decision.Disposition);
    }

    [Fact]
    public void CompletionAtExitGraceDeadlineWinsBeforeAbandonment()
    {
        var deadline = new DateTime(2026, 8, 2, 12, 0, 10, DateTimeKind.Utc);
        var completed = DadDutyLifecycleRules.ObserveDutyCompleted(
            enteredDuty: true,
            alreadyCompleted: false,
            freshCompletionEvidence: true);

        var decision = DadDutyLifecycleRules.EvaluateExit(
            true, completed, true, deadline, deadline, TimeSpan.FromSeconds(10));

        Assert.Equal(DadDutyExitDisposition.Completed, decision.Disposition);
    }

    [Fact]
    public void ExitWithoutCompletionRemainsAbandonedAfterGraceDeadline()
    {
        var deadline = new DateTime(2026, 8, 2, 12, 0, 10, DateTimeKind.Utc);
        var completed = DadDutyLifecycleRules.ObserveDutyCompleted(
            enteredDuty: true,
            alreadyCompleted: false,
            freshCompletionEvidence: false);

        var decision = DadDutyLifecycleRules.EvaluateExit(
            true, completed, true, deadline, deadline, TimeSpan.FromSeconds(10));

        Assert.Equal(DadDutyExitDisposition.Abandoned, decision.Disposition);
    }

    [Fact]
    public void ResetExitGraceHasNoDeadline()
        => Assert.False(DadDutyLifecycleRules.IsExitCompletionGraceExpired(DateTime.MinValue, DateTime.UtcNow));

    [Fact]
    public void QueueOwnershipRejectsContentionAndReleasesOnlyTheOwner()
    {
        var gate = new DadQueueOwnershipGate();

        Assert.Equal(DadQueueOwnershipClaim.Acquired, gate.TryClaim("run-a"));
        Assert.Equal(DadQueueOwnershipClaim.AlreadyOwned, gate.TryClaim("RUN-A"));
        Assert.Equal(DadQueueOwnershipClaim.Rejected, gate.TryClaim("run-b"));
        Assert.False(gate.Release("run-b"));
        Assert.Equal("run-a", gate.ActiveRunId);
        Assert.True(gate.Release("run-a"));
        Assert.False(gate.IsOwned);
        Assert.Equal(DadQueueOwnershipClaim.Acquired, gate.TryClaim("run-b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void QueueOwnershipRejectsMissingRunIds(string runId)
    {
        var gate = new DadQueueOwnershipGate();

        Assert.Equal(DadQueueOwnershipClaim.Rejected, gate.TryClaim(runId));
        Assert.False(gate.IsOwned);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void NativeAddonMutationRequiresVisibleAndReady(
        bool visible,
        bool ready,
        bool expected)
        => Assert.Equal(expected, DadDutyLifecycleRules.IsAddonReadyForMutation(visible, ready));

    [Fact]
    public void DurabilityExcludesSoulCrystalButKeepsZeroConditionGear()
    {
        var minimum = DadEquippedDurabilityMinimum.Empty;
        minimum = DadDutyLifecycleRules.ObserveEquippedDurability(
            minimum,
            DadDutyLifecycleRules.SoulCrystalEquippedSlotIndex,
            itemId: 1,
            condition: 0);
        Assert.False(minimum.Found);

        minimum = DadDutyLifecycleRules.ObserveEquippedDurability(
            minimum,
            slotIndex: 12,
            itemId: 2,
            condition: 0);
        Assert.True(minimum.Found);
        Assert.Equal(0, minimum.MinimumPercent);

        var ignoredEmptySlot = DadDutyLifecycleRules.ObserveEquippedDurability(
            minimum,
            slotIndex: 0,
            itemId: 0,
            condition: 30000);
        Assert.Equal(minimum, ignoredEmptySlot);
    }

    [Fact]
    public void MogtomeCorrelationRequiresExactCaseSensitiveRunIdentity()
    {
        Assert.True(DadMogtomeStatusRules.TryValidateRunId(
            "run-a",
            "run-a",
            "status",
            out var exactReason));
        Assert.Empty(exactReason);

        Assert.False(DadMogtomeStatusRules.TryValidateRunId(
            "run-a",
            "RUN-A",
            "status",
            out var mismatchReason));
        Assert.Contains("did not exactly match", mismatchReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, false, true, "", true)]
    [InlineData(false, true, false, true, "", false)]
    [InlineData(true, false, false, true, "", false)]
    [InlineData(true, true, true, true, "", false)]
    [InlineData(true, true, false, false, "", false)]
    [InlineData(true, true, false, true, "stop failed", false)]
    public void MogtomeStopAcknowledgementPreservesEveryFailure(
        bool accepted,
        bool dadOwned,
        bool isRunning,
        bool isTerminal,
        string failureReason,
        bool expected)
        => Assert.Equal(
            expected,
            DadMogtomeStatusRules.IsAcknowledgedStop(
                accepted,
                dadOwned,
                isRunning,
                isTerminal,
                failureReason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("hello")]
    [InlineData("/ays gc\n")]
    [InlineData("/ays gc\n/dad stop")]
    [InlineData("/ays gc\r/dad stop")]
    [InlineData("/ays\0gc")]
    public void NativeChatCommandRejectsMalformedInput(string? command)
    {
        Assert.False(DadNativeChatCommandRules.TryNormalize(command, out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("/pfinder", "/pfinder")]
    [InlineData("  /ays gc  ", "/ays gc")]
    [InlineData("/dad stop", "/dad stop")]
    public void NativeChatCommandAcceptsSingleLineSlashCommands(string command, string expected)
    {
        Assert.True(DadNativeChatCommandRules.TryNormalize(command, out var normalized, out var reason));
        Assert.Equal(expected, normalized);
        Assert.Empty(reason);
    }

    [Fact]
    public void ExecutorsReadFreshCompletionBeforeExitClassification()
    {
        AssertCompletionReadPrecedesExit(ReadRepositorySource("Services", "DadLocalDutyExecutor.cs"));
        AssertCompletionReadPrecedesExit(ReadRepositorySource("Services", "DadPremadeDutyExecutor.cs"));

        var moduleExecutors = ReadRepositorySource("Services", "DadModuleExecutors.cs");
        var dutySupport = Slice(moduleExecutors, "public sealed class DadDutySupportExecutor", "public sealed class DadTrustExecutor");
        var trust = Slice(moduleExecutors, "public sealed class DadTrustExecutor", "public sealed class DadBlundervilleExecutor");
        AssertCompletionReadPrecedesExit(dutySupport);
        AssertCompletionReadPrecedesExit(trust);
    }

    [Fact]
    public void EveryDutyExecutorUsesTheSameDelayedCompletionLifecycleRule()
    {
        var premade = ReadRepositorySource("Services", "DadPremadeDutyExecutor.cs");
        var local = ReadRepositorySource("Services", "DadLocalDutyExecutor.cs");
        var modules = ReadRepositorySource("Services", "DadModuleExecutors.cs");
        var dutySupport = Slice(modules, "public sealed class DadDutySupportExecutor", "public sealed class DadTrustExecutor");
        var trust = Slice(modules, "public sealed class DadTrustExecutor", "public sealed class DadBlundervilleExecutor");

        foreach (var source in new[] { premade, local, dutySupport, trust })
        {
            Assert.Contains("DadDutyLifecycleRules.EvaluateExit", source, StringComparison.Ordinal);
            Assert.Contains("ExitCompletionGraceDuration = TimeSpan.FromSeconds(10)", source, StringComparison.Ordinal);
            Assert.Contains("exitCompletionGraceUntilUtc = DateTime.MinValue", source, StringComparison.Ordinal);
            Assert.Contains("waiting for delayed completion", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QueueServicesReleaseOwnershipOnEntryAndRequireReadyAddons()
    {
        var local = NormalizeLines(ReadRepositorySource("Services", "DadLocalDutyQueueService.cs"));
        var npc = NormalizeLines(ReadRepositorySource("Services", "DadNpcDutyQueueService.cs"));
        var scanner = ReadRepositorySource("Services", "DadDutyFinderLiveEntryScanner.cs");

        Assert.Equal(2, CountOccurrences(local, "queueOwnership.Release();\n            return Active(content, DadLocalDutyQueuePulseKind.EnteredDuty"));
        Assert.Equal(1, CountOccurrences(npc, "queueOwnership.Release();\n            return Active(content, DadNpcDutyQueuePulseKind.EnteredDuty"));
        Assert.Contains("!addonBase->IsReady", local, StringComparison.Ordinal);
        Assert.Contains("IsAddonReadyForMutation(addon->IsVisible, addon->IsReady)", local, StringComparison.Ordinal);
        Assert.Contains("IsAddonReadyForMutation(addon->IsVisible, addon->IsReady)", npc, StringComparison.Ordinal);
        Assert.Contains("IsAddonReadyForMutation(addonBase->IsVisible, addonBase->IsReady)", scanner, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRegularAndRouletteNativeMutationHasFreshSafetyGate()
    {
        var source = ReadRepositorySource("Services", "DadLocalDutyQueueService.cs");

        AssertEveryOccurrenceHasNearbyGuard(source, "unrestrictedPartyLease.Ensure(", 500);
        AssertEveryOccurrenceHasNearbyGuard(source, "unrestrictedPartyLease.Restore(", 500);
        AssertEveryOccurrenceHasNearbyGuard(source, "agent->OpenRegularDuty(", 500);
        AssertEveryOccurrenceHasNearbyGuard(source, "agent->OpenRouletteDuty(", 500);
        AssertEveryOccurrenceHasNearbyGuard(source, "FireAddonIntCallback(addonBase, 12, 1)", 500);
        AssertEveryOccurrenceHasNearbyGuard(source, "FireAddonIntCallback(addonBase, 3,", 500);
        AssertEveryOccurrenceHasNearbyGuard(source, "FireAddonIntCallback(addonBase, 12, 0)", 500);
    }

    [Fact]
    public void CompletionCommandsUseRegisteredPluginPathAndReserveNativeForGrandCompany()
    {
        var source = ReadRepositorySource("Services", "DadCompletionActionRunner.cs");

        Assert.Contains("NativeCommandExecutor.TryExecute", source, StringComparison.Ordinal);
        Assert.Contains("Plugin.CommandManager.ProcessCommand", source, StringComparison.Ordinal);
        Assert.Contains("TryNormalizeGrandCompanyHandInCommand", source, StringComparison.Ordinal);
        Assert.Contains("dad-completion-native-command-timeout", source, StringComparison.Ordinal);
    }

    private static void AssertCompletionReadPrecedesExit(string source)
    {
        var evidence = source.IndexOf("freshCompletionEvidence", StringComparison.Ordinal);
        var exit = source.IndexOf("exitedRequestedDuty", evidence, StringComparison.Ordinal);
        Assert.True(evidence >= 0);
        Assert.True(exit > evidence);
    }

    private static void AssertEveryOccurrenceHasNearbyGuard(
        string source,
        string mutation,
        int maximumDistance)
    {
        var searchFrom = 0;
        var found = 0;
        while (true)
        {
            var mutationIndex = source.IndexOf(mutation, searchFrom, StringComparison.Ordinal);
            if (mutationIndex < 0)
                break;

            var guardIndex = source.LastIndexOf("TryGetMutationSafety", mutationIndex, StringComparison.Ordinal);
            Assert.True(guardIndex >= 0, $"Missing safety gate before {mutation}.");
            Assert.InRange(mutationIndex - guardIndex, 1, maximumDistance);
            found++;
            searchFrom = mutationIndex + mutation.Length;
        }

        Assert.True(found > 0, $"Expected at least one mutation matching {mutation}.");
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

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
}
