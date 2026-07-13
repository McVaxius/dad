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

    private static DadNativePartyInviteTarget Target(
        uint localCurrentWorldId,
        ushort targetWorldId,
        bool sameApplicableInstanceExact = false)
        => new()
        {
            RunId = "run",
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = "Slot2",
            AccountKey = new DadAccountKey("account-x"),
            CharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            ContentId = 200,
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
