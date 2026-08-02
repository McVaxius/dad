using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadNativePartyInviteRulesTests
{
    private static readonly DateTime Start = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExcaliburToExcaliburDispatchesExactSameWorldIdentity()
    {
        var dispatcher = new FakeDispatcher(true);
        var tracker = new DadNativePartyInviteAttemptTracker();
        var target = Target(localCurrentWorldId: 21, targetWorldId: 21);

        var attempt = tracker.TryDispatch(target, false, Start, dispatcher, out var blocker);

        Assert.Equal(string.Empty, blocker);
        Assert.NotNull(attempt);
        Assert.Equal(DadNativePartyInviteType.SameWorld, attempt.InviteType);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(200ul, dispatcher.ContentId);
        Assert.Equal("Hard'carry Gray'parse", dispatcher.CharacterName);
        Assert.Equal((ushort)21, dispatcher.WorldId);
        Assert.Equal("same", dispatcher.Operation);
    }

    [Fact]
    public void DifferentCurrentWorldDispatchesContentIdWithZeroWorld()
    {
        var dispatcher = new FakeDispatcher(true);
        var tracker = new DadNativePartyInviteAttemptTracker();

        var attempt = tracker.TryDispatch(
            Target(localCurrentWorldId: 22, targetWorldId: 21),
            false,
            Start,
            dispatcher,
            out var blocker);

        Assert.Equal(string.Empty, blocker);
        Assert.Equal(DadNativePartyInviteType.CrossWorldContentId, attempt?.InviteType);
        Assert.Equal("cross", dispatcher.Operation);
        Assert.Equal(200ul, dispatcher.ContentId);
        Assert.Equal((ushort)0, dispatcher.WorldId);
    }

    [Fact]
    public void InInstanceDispatchRequiresExactApplicableInstanceTruth()
    {
        var exactDispatcher = new FakeDispatcher(true);
        var exactTracker = new DadNativePartyInviteAttemptTracker();
        var exact = exactTracker.TryDispatch(
            Target(21, 21, sameApplicableInstanceExact: true),
            false,
            Start,
            exactDispatcher,
            out _);

        var ordinaryDispatcher = new FakeDispatcher(true);
        var ordinaryTracker = new DadNativePartyInviteAttemptTracker();
        var ordinary = ordinaryTracker.TryDispatch(
            Target(21, 21, sameApplicableInstanceExact: false),
            false,
            Start,
            ordinaryDispatcher,
            out _);

        Assert.Equal(DadNativePartyInviteType.InInstanceContentId, exact?.InviteType);
        Assert.Equal("instance", exactDispatcher.Operation);
        Assert.Equal(DadNativePartyInviteType.SameWorld, ordinary?.InviteType);
        Assert.Equal("same", ordinaryDispatcher.Operation);
    }

    [Fact]
    public void AmbiguousWorldTruthUsesLikelyBranchThenAlternateAfterFiveSeconds()
    {
        var dispatcher = new FakeDispatcher(true);
        var tracker = new DadNativePartyInviteAttemptTracker();
        var target = Target(21, 21);

        var first = tracker.TryDispatch(target, false, Start, dispatcher, out _);
        var early = tracker.TryDispatch(target, false, Start.AddSeconds(4.999), dispatcher, out _);
        var second = tracker.TryDispatch(target, false, Start.AddSeconds(5), dispatcher, out _);

        Assert.Equal(DadNativePartyInviteType.SameWorld, first?.InviteType);
        Assert.Null(early);
        Assert.Equal(DadNativePartyInviteType.CrossWorldContentId, second?.InviteType);
        Assert.Equal(2, dispatcher.CallCount);
        Assert.Equal((ushort)0, dispatcher.WorldId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchBooleanNeverCountsAsMembershipAndRetriesAfterFiveSeconds(bool dispatchResult)
    {
        var dispatcher = new FakeDispatcher(dispatchResult);
        var tracker = new DadNativePartyInviteAttemptTracker();
        var target = Target(21, 21);

        var first = tracker.TryDispatch(target, false, Start, dispatcher, out _);
        var second = tracker.TryDispatch(target, false, Start.AddSeconds(5), dispatcher, out _);

        Assert.Equal(dispatchResult, first?.DispatchResult);
        Assert.Equal(dispatchResult, second?.DispatchResult);
        Assert.Equal(2, dispatcher.CallCount);
    }

    [Fact]
    public void EveryMissingFrozenParticipantHasAnIndependentUnboundedInviteCadence()
    {
        var dispatcher = new FakeDispatcher(true);
        var tracker = new DadNativePartyInviteAttemptTracker();
        var targets = new[]
        {
            Target(21, 21, contentId: 200, slotId: "Slot2"),
            Target(21, 21, contentId: 300, slotId: "Slot3"),
            Target(21, 21, contentId: 400, slotId: "Slot4"),
        };

        foreach (var target in targets)
            Assert.Equal(1, tracker.TryDispatch(target, false, Start, dispatcher, out _)?.AttemptNumber);
        foreach (var target in targets)
            Assert.Equal(2, tracker.TryDispatch(target, false, Start.AddSeconds(5), dispatcher, out _)?.AttemptNumber);
        foreach (var target in targets)
            Assert.Equal(3, tracker.TryDispatch(target, false, Start.AddSeconds(10), dispatcher, out _)?.AttemptNumber);

        Assert.Equal(9, dispatcher.CallCount);
    }

    [Fact]
    public void ExactPartyListContentIdStopsAttemptsAndRunConfirmationIsConsumedOnce()
    {
        var dispatcher = new FakeDispatcher(true);
        var tracker = new DadNativePartyInviteAttemptTracker();
        var target = Target(21, 21);
        tracker.TryDispatch(target, false, Start, dispatcher, out _);

        var afterMembership = tracker.TryDispatch(target, true, Start.AddSeconds(5), dispatcher, out _);
        var advancesOnce = tracker.ConfirmRun("run");
        var cannotAdvanceAgain = tracker.ConfirmRun("run");
        var afterConfirmation = tracker.TryDispatch(target, false, Start.AddSeconds(10), dispatcher, out _);

        Assert.Null(afterMembership);
        Assert.True(advancesOnce);
        Assert.False(cannotAdvanceAgain);
        Assert.Null(afterConfirmation);
        Assert.Equal(1, dispatcher.CallCount);
    }

    [Fact]
    public void DispatcherSurfaceContainsOnlyNativeInviteOperations()
    {
        var methods = typeof(IDadNativePartyInviteDispatcher).GetMethods();

        Assert.Equal(
            ["InviteCrossWorld", "InviteInInstance", "InviteSameWorld"],
            methods.Select(static method => method.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(methods, static method =>
            method.GetParameters().Any(parameter => parameter.ParameterType.Name.Contains("Command", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ParticipantAcceptsOnlyFreshExactFrozenInviterInvitation()
    {
        var tracker = new DadPartyInvitationAcceptanceTracker();
        var baseline = new DadPendingPartyInvitation(40, "Old Inviter", 21);
        tracker.BeginRun("run", baseline);
        Assert.True(tracker.TryArm(ExpectedInviter(), out var blocker));
        Assert.Equal(string.Empty, blocker);

        Assert.False(tracker.ShouldAccept(baseline, false, Start));
        Assert.False(tracker.ShouldAccept(new DadPendingPartyInvitation(41, "Wrong Inviter", 21), false, Start));
        Assert.False(tracker.ShouldAccept(new DadPendingPartyInvitation(42, "Warm Heart", 22), false, Start));

        var fresh = new DadPendingPartyInvitation(43, "Warm Heart", 21);
        Assert.True(tracker.ShouldAccept(fresh, false, Start));
        tracker.RecordAttempt(fresh, Start);
        Assert.False(tracker.ShouldAccept(fresh, false, Start.AddSeconds(4.999)));
        Assert.True(tracker.ShouldAccept(fresh, false, Start.AddSeconds(5)));
        Assert.False(tracker.ShouldAccept(fresh, true, Start.AddSeconds(5)));
        Assert.False(tracker.ShouldAccept(new DadPendingPartyInvitation(44, "Warm Heart", 21), false, Start.AddSeconds(10)));
    }

    [Fact]
    public void ExpectedInviterIdentityCannotBeSubstitutedAfterArming()
    {
        var tracker = new DadPartyInvitationAcceptanceTracker();
        tracker.BeginRun("run", default);
        Assert.True(tracker.TryArm(ExpectedInviter(), out _));
        var replacement = ExpectedInviter();
        replacement = new DadExpectedPartyInviter
        {
            RunId = replacement.RunId,
            WorkerSessionId = replacement.WorkerSessionId,
            AccountKey = replacement.AccountKey,
            CharacterKey = new DadCharacterKey("Replacement@Excalibur"),
            ContentId = replacement.ContentId,
            CharacterName = replacement.CharacterName,
            WorldId = replacement.WorldId,
        };

        Assert.False(tracker.TryArm(replacement, out var blocker));
        Assert.Contains("changed", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HiddenPromptRestorationRequiresFreshExactPendingInvitation()
    {
        var expected = ExpectedInviter();
        var exact = new DadPendingPartyInvitation(43, expected.CharacterName, expected.WorldId);
        var hidden = new DadSelectYesnoPromptSnapshot(false, false, string.Empty, string.Empty);

        Assert.True(DadPartyInvitePromptOwnershipRules.ShouldRestoreHiddenPrompt(exact, expected, hidden));
        Assert.False(DadPartyInvitePromptOwnershipRules.ShouldRestoreHiddenPrompt(
            new DadPendingPartyInvitation(44, "Wrong Inviter", expected.WorldId),
            expected,
            hidden));
        Assert.False(DadPartyInvitePromptOwnershipRules.ShouldRestoreHiddenPrompt(
            exact,
            expected,
            new DadSelectYesnoPromptSnapshot(true, true, "existing", "Unrelated confirmation")));
    }

    [Fact]
    public void DirectYesRequiresDadOwnedSurfaceOrExactInviterPrompt()
    {
        var expected = ExpectedInviter();
        var exact = new DadPendingPartyInvitation(43, expected.CharacterName, expected.WorldId);
        var hidden = new DadSelectYesnoPromptSnapshot(false, false, string.Empty, string.Empty);
        var surfaced = new DadSelectYesnoPromptSnapshot(true, true, "surface", "Join the party?");
        var exactPrompt = new DadSelectYesnoPromptSnapshot(true, true, "exact", $"Join {expected.CharacterName}'s party?");
        var unrelated = new DadSelectYesnoPromptSnapshot(true, true, "unrelated", "Discard this item?");

        Assert.True(DadPartyInvitePromptOwnershipRules.CanUseDirectYes(
            exact, exact, expected, hidden, hidden, exactPrompt, restoreDispatched: true,
            currentAttempt: 1, approvedAttempt: 0, soleReadyPrompt: true,
            allowFreshUnprovenPromptApproval: false, out _));
        Assert.True(DadPartyInvitePromptOwnershipRules.CanUseDirectYes(
            exact, exact, expected, hidden, unrelated, exactPrompt, restoreDispatched: false,
            currentAttempt: 1, approvedAttempt: 0, soleReadyPrompt: true,
            allowFreshUnprovenPromptApproval: false, out _));
        Assert.False(DadPartyInvitePromptOwnershipRules.CanUseDirectYes(
            exact, exact, expected, hidden, unrelated, unrelated, restoreDispatched: false,
            currentAttempt: 1, approvedAttempt: 0, soleReadyPrompt: true,
            allowFreshUnprovenPromptApproval: false, out _));
        Assert.False(DadPartyInvitePromptOwnershipRules.CanUseDirectYes(
            exact, exact, expected, exactPrompt, exactPrompt, exactPrompt, restoreDispatched: false,
            currentAttempt: 1, approvedAttempt: 0, soleReadyPrompt: true,
            allowFreshUnprovenPromptApproval: false, out _));
        Assert.False(DadPartyInvitePromptOwnershipRules.CanUseDirectYes(
            exact,
            new DadPendingPartyInvitation(44, expected.CharacterName, expected.WorldId),
            expected,
            hidden,
            hidden,
            surfaced,
            restoreDispatched: true,
            currentAttempt: 1,
            approvedAttempt: 0,
            soleReadyPrompt: true,
            allowFreshUnprovenPromptApproval: true,
            out _));
    }

    [Fact]
    public void AssemblyWindowPublishesWarningWithoutTerminatingInviteRetries()
    {
        var tracker = new DadPartyInvitationAcceptanceTracker();
        var expected = ExpectedInviter();
        var invitation = new DadPendingPartyInvitation(43, expected.CharacterName, expected.WorldId);
        tracker.BeginRun("run", default);
        Assert.True(tracker.TryArm(expected, out _));
        Assert.True(tracker.ShouldAccept(invitation, false, Start));
        tracker.RecordAttempt(invitation, Start);

        var active = tracker.BuildRetryStatus(Start.AddSeconds(119.999), TimeSpan.FromSeconds(120));
        var warning = tracker.BuildRetryStatus(Start.AddSeconds(120), TimeSpan.FromSeconds(120));
        var coordinatorWarning = DadPartyInvitationRetryRules.BuildWarning(
            new DadCharacterKey("Participant@World"),
            expected.CharacterKey);

        Assert.True(DadPartyInvitationRetryRules.IsContinuingRetry(active));
        Assert.False(DadPartyInvitationRetryRules.IsPersistentWarning(active));
        Assert.True(DadPartyInvitationRetryRules.IsPersistentWarning(warning));
        Assert.Contains("has not accepted", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remains reachable", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retries continue", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Participant@World", coordinatorWarning, StringComparison.Ordinal);
        Assert.True(DadPartyInvitationRetryRules.IsPersistentWarning(coordinatorWarning));
        Assert.False(DadPartyInvitationRetryRules.ShouldApplyAssemblyTimeout(false, true, [active]));
        Assert.False(DadPartyInvitationRetryRules.ShouldApplyAssemblyTimeout(false, true, [warning]));
        Assert.True(DadPartyInvitationRetryRules.ShouldApplyAssemblyTimeout(false, true, ["Unrelated assembly blocker"]));
        Assert.False(DadPartyInvitationRetryRules.ShouldApplyAssemblyTimeout(true, true, ["Unrelated assembly blocker"]));

        Assert.True(tracker.ShouldAccept(invitation, false, Start.AddSeconds(120)));
    }

    private static DadNativePartyInviteTarget Target(
        uint localCurrentWorldId,
        ushort targetWorldId,
        bool sameApplicableInstanceExact = false,
        ulong contentId = 200,
        string slotId = "Slot2")
        => new()
        {
            RunId = "run",
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = slotId,
            AccountKey = new DadAccountKey("account-x"),
            CharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            ContentId = contentId,
            CharacterName = "Hard'carry Gray'parse",
            WorldId = targetWorldId,
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            LocalCurrentWorldId = localCurrentWorldId,
            WorldRelationExact = false,
            SameApplicableInstanceExact = sameApplicableInstanceExact,
        };

    private static DadExpectedPartyInviter ExpectedInviter()
        => new()
        {
            RunId = "run",
            WorkerSessionId = new DadWorkerSessionId("worker-w"),
            AccountKey = new DadAccountKey("account-w"),
            CharacterKey = new DadCharacterKey("Warm Heart@Excalibur"),
            ContentId = 100,
            CharacterName = "Warm Heart",
            WorldId = 21,
        };

    private sealed class FakeDispatcher(bool result) : IDadNativePartyInviteDispatcher
    {
        public int CallCount { get; private set; }
        public string Operation { get; private set; } = string.Empty;
        public ulong ContentId { get; private set; }
        public string CharacterName { get; private set; } = string.Empty;
        public ushort WorldId { get; private set; }

        public bool InviteSameWorld(ulong contentId, string exactCharacterName, ushort worldId)
        {
            Record("same", contentId, exactCharacterName, worldId);
            return result;
        }

        public bool InviteCrossWorld(ulong contentId, ushort worldId)
        {
            Record("cross", contentId, string.Empty, worldId);
            return result;
        }

        public bool InviteInInstance(ulong contentId)
        {
            Record("instance", contentId, string.Empty, 0);
            return result;
        }

        private void Record(string operation, ulong contentId, string name, ushort worldId)
        {
            CallCount++;
            Operation = operation;
            ContentId = contentId;
            CharacterName = name;
            WorldId = worldId;
        }
    }
}
