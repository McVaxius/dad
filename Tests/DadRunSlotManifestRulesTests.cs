using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRunSlotManifestRulesTests
{
    private const string WAccount = "account-w";
    private const string XAccount = "account-x";
    private const string WCharacter = "W Character@Alpha";
    private const string XCharacter = "X Character@Alpha";
    private const ulong WContentId = 1001;
    private const ulong XContentId = 2002;

    [Fact]
    public void ReversedRuntimeOrderingPreservesWSlot1AndXSlot2()
    {
        var plan = BuildPremadeDutyPlan();
        Assert.True(DadRunSlotManifestRules.TryCreate(plan, out var manifest, out var createBlocker), createBlocker);

        var w = Participant(WAccount, WCharacter, WContentId, "worker-w");
        var x = Participant(XAccount, XCharacter, XContentId, "worker-x");
        var reversedRuntime = new[] { x, w };

        Assert.True(
            DadRunSlotManifestRules.TryBindWorkerSessions(manifest, reversedRuntime, out var bound, out var bindBlocker),
            bindBlocker);

        Assert.Collection(
            bound.Slots,
            slot =>
            {
                Assert.Equal("Slot1", slot.SlotId);
                Assert.Equal(WAccount, slot.AccountKey.Value);
                Assert.Equal(WCharacter, slot.CharacterKey.Value);
                Assert.Equal(WContentId, slot.ContentId);
                Assert.Equal("worker-w", slot.WorkerSessionId.Value);
                Assert.True(slot.IsLeader);
                Assert.True(slot.IsInviter);
            },
            slot =>
            {
                Assert.Equal("Slot2", slot.SlotId);
                Assert.Equal(XAccount, slot.AccountKey.Value);
                Assert.Equal(XCharacter, slot.CharacterKey.Value);
                Assert.Equal(XContentId, slot.ContentId);
                Assert.Equal("worker-x", slot.WorkerSessionId.Value);
                Assert.False(slot.IsLeader);
                Assert.False(slot.IsInviter);
            });

        var resolvedW = DadRunSlotManifestRules.ResolveSlot(bound.Slots[0], reversedRuntime, true, out var wBlocker);
        var resolvedX = DadRunSlotManifestRules.ResolveSlot(bound.Slots[1], reversedRuntime, true, out var xBlocker);

        Assert.Equal(string.Empty, wBlocker);
        Assert.Equal(string.Empty, xBlocker);
        Assert.Equal("Slot1", resolvedW.AssignedSlotId);
        Assert.Equal("worker-w", resolvedW.WorkerSessionId.Value);
        Assert.Equal("Slot2", resolvedX.AssignedSlotId);
        Assert.Equal("worker-x", resolvedX.WorkerSessionId.Value);
    }

    [Theory]
    [MemberData(nameof(MultiplayerPayloadCases))]
    public void MultiplayerLanePayloadIsFrozenExactly(
        string caseName,
        DadRunPlan plan,
        DadModuleId expectedModule,
        string expectedDuty,
        uint expectedCfc,
        uint expectedRoulette,
        bool expectedUnsynced,
        int expectedPartySize)
    {
        _ = caseName;
        Assert.True(DadRunSlotManifestRules.TryCreate(plan, out var manifest, out var blocker), blocker);

        var payload = Assert.Single(manifest.Modules);
        Assert.Equal(expectedModule, payload.ModuleId);
        Assert.Equal(expectedDuty, payload.DutyName);
        Assert.Equal(expectedCfc, payload.ContentFinderConditionId);
        Assert.Equal(expectedRoulette, payload.RouletteId);
        Assert.Equal(expectedUnsynced, payload.Unsynced);
        Assert.Equal(expectedPartySize, payload.ExpectedPartySize);
        Assert.Equal(expectedPartySize, manifest.ExpectedPartySize);
        Assert.Equal(expectedPartySize, manifest.Slots.Count);
    }

    [Fact]
    public void IncompleteTypedRosterIsRejected()
    {
        var missingRow = BuildPremadeDutyPlan();
        missingRow.Orchestration.RequiredRosterCharacters.RemoveAt(1);

        Assert.False(DadRunSlotManifestRules.TryCreate(missingRow, out _, out var countBlocker));
        Assert.Contains("complete typed roster", countBlocker, StringComparison.OrdinalIgnoreCase);

        var missingAccount = BuildPremadeDutyPlan();
        missingAccount.Orchestration.RequiredRosterCharacters[1].AccountKey = new DadAccountKey(string.Empty);
        Assert.False(DadRunSlotManifestRules.TryCreate(missingAccount, out _, out var accountBlocker));
        Assert.Contains("Slot2", accountBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("managed account", accountBlocker, StringComparison.OrdinalIgnoreCase);

        var missingCharacter = BuildPremadeDutyPlan();
        missingCharacter.Orchestration.RequiredRosterCharacters[1].CharacterKey = new DadCharacterKey(string.Empty);
        Assert.False(DadRunSlotManifestRules.TryCreate(missingCharacter, out _, out var characterBlocker));
        Assert.Contains("Slot2", characterBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("character key", characterBlocker, StringComparison.OrdinalIgnoreCase);

        var missingContentId = BuildPremadeDutyPlan();
        missingContentId.Orchestration.RequiredRosterCharacters[1].ContentId = 0;
        Assert.False(DadRunSlotManifestRules.TryCreate(missingContentId, out _, out var contentIdBlocker));
        Assert.Contains("Slot2", contentIdBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content ID", contentIdBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateAccountsAndCharacterIdentitiesAreRejected()
    {
        var duplicateAccount = BuildPremadeDutyPlan();
        duplicateAccount.Orchestration.RequiredRosterCharacters[1].AccountKey = new DadAccountKey(WAccount);

        Assert.False(DadRunSlotManifestRules.TryCreate(duplicateAccount, out _, out var accountBlocker));
        Assert.Contains("more than one frozen slot", accountBlocker, StringComparison.OrdinalIgnoreCase);

        var duplicateCharacter = BuildPremadeDutyPlan();
        duplicateCharacter.Orchestration.RequiredRosterCharacters[1].CharacterKey = new DadCharacterKey("Renamed Character@Alpha");
        duplicateCharacter.Orchestration.RequiredRosterCharacters[1].ContentId = WContentId;

        Assert.False(DadRunSlotManifestRules.TryCreate(duplicateCharacter, out _, out var characterBlocker));
        Assert.Contains("more than one frozen slot", characterBlocker, StringComparison.OrdinalIgnoreCase);

        var duplicateCharacterKey = BuildPremadeDutyPlan();
        duplicateCharacterKey.Orchestration.RequiredRosterCharacters[1].CharacterKey = new DadCharacterKey(WCharacter);
        duplicateCharacterKey.Orchestration.RequiredRosterCharacters[1].ContentId = 9090;

        Assert.False(DadRunSlotManifestRules.TryCreate(duplicateCharacterKey, out _, out var characterKeyBlocker));
        Assert.Contains("more than one frozen slot", characterKeyBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContradictoryRosterAndModuleSizesAreRejected()
    {
        var rosterIntentMismatch = BuildPremadeDutyPlan();
        rosterIntentMismatch.Orchestration.RosterIntent.ExpectedPartySize = 3;

        Assert.False(DadRunSlotManifestRules.TryCreate(rosterIntentMismatch, out _, out var rosterBlocker));
        Assert.Contains("party-size contradiction", rosterBlocker, StringComparison.OrdinalIgnoreCase);

        var moduleMismatch = BuildPremadeDutyPlan();
        moduleMismatch.Modules[0].ExpectedPartySize = 3;

        Assert.False(DadRunSlotManifestRules.TryCreate(moduleMismatch, out _, out var moduleBlocker));
        Assert.Contains("payload party size 2", moduleBlocker, StringComparison.OrdinalIgnoreCase);

        var wrongLeader = BuildPremadeDutyPlan();
        wrongLeader.LeaderCharacterKey = XCharacter;

        Assert.False(DadRunSlotManifestRules.TryCreate(wrongLeader, out _, out var leaderBlocker));
        Assert.Contains("Slot1", leaderBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("leader", leaderBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindingNeverUsesAliasesOrAvailableCharacterProjections()
    {
        var plan = BuildPremadeDutyPlan();
        Assert.True(DadRunSlotManifestRules.TryCreate(plan, out var manifest, out var createBlocker), createBlocker);

        var w = Participant(WAccount, WCharacter, WContentId, "worker-w");
        var projectedX = Participant("different-stable-account", "Other Character@Alpha", 9999, "worker-other");
        projectedX.ManagedAccountAlias = XAccount;
        projectedX.AvailableCharacterKeys = [new DadCharacterKey(XCharacter)];

        Assert.False(
            DadRunSlotManifestRules.TryBindWorkerSessions(manifest, [projectedX, w], out _, out var blocker));
        Assert.Contains("Slot2", blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(XAccount, blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FrozenSlotWaitsThroughDisconnectWrongCharacterAndPostArRelog()
    {
        var slot = BindXSlot();

        var disconnected = DadRunSlotManifestRules.ResolveSlot(slot, [], true, out var disconnectBlocker);
        Assert.Equal(DadParticipantState.Stale, disconnected.State);
        Assert.Equal("Slot2", disconnected.AssignedSlotId);
        Assert.Equal("worker-x", disconnected.WorkerSessionId.Value);
        Assert.Equal(XCharacter, disconnected.DesiredCharacterKey);
        Assert.True(disconnected.ActiveCharacterKey.IsEmpty);
        Assert.Contains("Slot2", disconnectBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("worker-x", disconnectBlocker, StringComparison.OrdinalIgnoreCase);

        var wrongCharacter = Participant(XAccount, "Relog Target@Alpha", 3333, "worker-x");
        wrongCharacter.AvailableCharacterKeys = [new DadCharacterKey(XCharacter)];
        var relogging = DadRunSlotManifestRules.ResolveSlot(slot, [wrongCharacter], true, out var relogBlocker);
        Assert.Equal(DadParticipantState.WaitingForRequiredCharacter, relogging.State);
        Assert.Equal("Slot2", relogging.AssignedSlotId);
        Assert.Equal(XCharacter, relogging.DesiredCharacterKey);
        Assert.Equal("Relog Target@Alpha", relogging.ActiveCharacterKey.Value);
        Assert.Contains(XCharacter, relogBlocker, StringComparison.OrdinalIgnoreCase);

        var loading = Participant(XAccount, XCharacter, XContentId, "worker-x", postArReady: false);
        var postArWait = DadRunSlotManifestRules.ResolveSlot(slot, [loading], true, out var postArBlocker);
        Assert.Equal(DadParticipantState.WaitingForPostArReady, postArWait.State);
        Assert.Contains("post-AR", postArBlocker, StringComparison.OrdinalIgnoreCase);

        var ready = Participant(XAccount, XCharacter, XContentId, "worker-x");
        var resolved = DadRunSlotManifestRules.ResolveSlot(slot, [ready], true, out var readyBlocker);
        Assert.Equal(string.Empty, readyBlocker);
        Assert.Equal(DadParticipantState.Discovered, resolved.State);
        Assert.Equal("Slot2", resolved.AssignedSlotId);
        Assert.Equal("worker-x", resolved.WorkerSessionId.Value);
    }

    [Fact]
    public void SameCharacterKeyWithWrongContentIdDoesNotResolve()
    {
        var slot = BindXSlot();
        var wrongContentId = Participant(XAccount, XCharacter, 999999, "worker-x");

        var resolved = DadRunSlotManifestRules.ResolveSlot(slot, [wrongContentId], true, out var blocker);

        Assert.Equal(DadParticipantState.WaitingForRequiredCharacter, resolved.State);
        Assert.Equal("Slot2", resolved.AssignedSlotId);
        Assert.Contains(XContentId.ToString(), blocker, StringComparison.Ordinal);
        Assert.Contains("999999", blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitSelectedSubstituteBecomesTheFrozenSlotIdentity()
    {
        const string primaryAccount = "account-primary-x";
        const string substituteAccount = "account-substitute-x";
        const string substituteCharacter = "Substitute X@Alpha";
        const ulong substituteContentId = 7007;

        var plan = BuildPremadeDutyPlan();
        plan.Orchestration.RequiredRosterCharacters[1] = RosterRef(
            substituteAccount,
            substituteCharacter,
            substituteContentId);

        Assert.True(DadRunSlotManifestRules.TryCreate(plan, out var manifest, out var createBlocker), createBlocker);

        var primary = Participant(primaryAccount, XCharacter, XContentId, "worker-primary");
        primary.ManagedAccountAlias = substituteAccount;
        primary.AvailableCharacterKeys = [new DadCharacterKey(substituteCharacter)];
        var substitute = Participant(substituteAccount, substituteCharacter, substituteContentId, "worker-substitute");
        var w = Participant(WAccount, WCharacter, WContentId, "worker-w");

        Assert.True(
            DadRunSlotManifestRules.TryBindWorkerSessions(
                manifest,
                [primary, substitute, w],
                out var bound,
                out var bindBlocker),
            bindBlocker);

        var slot2 = bound.Slots[1];
        Assert.Equal("Slot2", slot2.SlotId);
        Assert.Equal(substituteAccount, slot2.AccountKey.Value);
        Assert.Equal(substituteCharacter, slot2.CharacterKey.Value);
        Assert.Equal(substituteContentId, slot2.ContentId);
        Assert.Equal("worker-substitute", slot2.WorkerSessionId.Value);
    }

    public static IEnumerable<object[]> MultiplayerPayloadCases()
    {
        yield return PayloadCase(
            "Premade Duty",
            BuildPremadeDutyPlan(),
            DadModuleId.PremadeDuty,
            "Sastasha",
            4,
            0,
            true,
            2);

        yield return PayloadCase(
            "Premade Dungeon",
            BuildPlan(
                DadModuleId.Duty,
                4,
                request => request.Dungeon = new DadDungeonTask
                {
                    SelectedDungeon = "Sastasha",
                    ContentFinderConditionId = 4,
                    QueueViaLanParty = true,
                    Unsynced = true,
                }),
            DadModuleId.Duty,
            "Sastasha",
            4,
            0,
            true,
            4);

        yield return PayloadCase(
            "Daily MSQ",
            BuildPlan(
                DadModuleId.DailyMsq,
                4,
                request => request.DailyMsq = new DadDailyMsqTask
                {
                    QueueTarget = new DadQueueTarget
                    {
                        Kind = DadQueueTargetKind.Roulette,
                        RouletteId = 5,
                        Key = "MainScenario",
                        DisplayName = "Main Scenario Roulette",
                    },
                }),
            DadModuleId.DailyMsq,
            "Main Scenario Roulette",
            0,
            5,
            false,
            4);

        yield return PayloadCase(
            "Commendation",
            BuildPlan(
                DadModuleId.Commendation,
                4,
                request => request.Commendation = new DadCommendationTask
                {
                    DutyName = "Under the Armour",
                    ContentFinderConditionId = 52,
                }),
            DadModuleId.Commendation,
            "Under the Armour",
            52,
            0,
            false,
            4);

        yield return PayloadCase(
            "Astrope",
            BuildPlan(
                DadModuleId.Astrope,
                4,
                request => request.Astrope = new DadAstropeTask
                {
                    QueueTarget = new DadQueueTarget
                    {
                        Kind = DadQueueTargetKind.Roulette,
                        RouletteId = 7,
                        Key = "Mentor",
                        DisplayName = "Mentor Roulette",
                    },
                }),
            DadModuleId.Astrope,
            "Mentor Roulette",
            0,
            7,
            false,
            4);

        yield return PayloadCase(
            "Custom Duty",
            BuildPlan(
                DadModuleId.CustomDuty,
                3,
                request => request.CustomDuty = new DadCustomDutyTask
                {
                    DutyName = "The Bowl of Embers",
                    ContentFinderConditionId = 16,
                    ExpectedPartySize = 3,
                    Unsynced = true,
                }),
            DadModuleId.CustomDuty,
            "The Bowl of Embers",
            16,
            0,
            true,
            3);

        yield return PayloadCase(
            "Party Variant/VVD",
            BuildPlan(
                DadModuleId.VariantVvd,
                3,
                request => request.VariantVvd = new DadVariantVvdTask
                {
                    DutyName = "The Sil'dihn Subterrane",
                    ContentFinderConditionId = 1069,
                    ExpectedPartySize = 3,
                    Unsynced = false,
                }),
            DadModuleId.VariantVvd,
            "The Sil'dihn Subterrane",
            1069,
            0,
            false,
            3);
    }

    private static object[] PayloadCase(
        string name,
        DadRunPlan plan,
        DadModuleId module,
        string duty,
        uint cfc,
        uint roulette,
        bool unsynced,
        int partySize)
        => [name, plan, module, duty, cfc, roulette, unsynced, partySize];

    private static DadFrozenRunSlot BindXSlot()
    {
        var plan = BuildPremadeDutyPlan();
        Assert.True(DadRunSlotManifestRules.TryCreate(plan, out var manifest, out var createBlocker), createBlocker);
        Assert.True(
            DadRunSlotManifestRules.TryBindWorkerSessions(
                manifest,
                [
                    Participant(XAccount, XCharacter, XContentId, "worker-x"),
                    Participant(WAccount, WCharacter, WContentId, "worker-w"),
                ],
                out var bound,
                out var bindBlocker),
            bindBlocker);
        return bound.Slots[1];
    }

    private static DadRunPlan BuildPremadeDutyPlan()
        => BuildPlan(
            DadModuleId.PremadeDuty,
            2,
            request => request.PremadeDuty = new DadPremadeDutyTask
            {
                DutyName = "Sastasha",
                ContentFinderConditionId = 4,
                ExpectedPartySize = 2,
                Attempts = 1,
                Unsynced = true,
            });

    private static DadRunPlan BuildPlan(
        DadModuleId moduleId,
        int partySize,
        Action<DadRunRequest> configureRequest)
    {
        var orchestration = new DadOrchestrationIntent
        {
            ModuleTarget = moduleId,
            PreferredLeaderCharacterKey = new DadCharacterKey(WCharacter),
            PreferredInviterCharacterKey = new DadCharacterKey(WCharacter),
            RequiredRosterCharacters = BuildRoster(partySize),
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = partySize,
                RequireRemoteParticipants = true,
                RequireExactCharacters = true,
                AllowStoredXadbFallback = false,
            },
        };
        var request = new DadRunRequest
        {
            RequestId = $"request-{moduleId}-{partySize}",
            RequestedBy = "manifest-tests",
            Orchestration = orchestration,
        };
        configureRequest(request);

        return new DadRunPlan
        {
            Request = request,
            CompositeModuleId = moduleId,
            Orchestration = orchestration,
            RequiredParticipantCount = partySize,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = WCharacter,
            InviterCharacterKey = WCharacter,
            Modules =
            [
                new DadPlannedModuleExecution
                {
                    ModuleId = moduleId,
                    DisplayName = moduleId.ToString(),
                    ExpectedPartySize = partySize,
                    RequiresPeers = true,
                },
            ],
        };
    }

    private static List<DadRosterCharacterRef> BuildRoster(int partySize)
    {
        var roster = new List<DadRosterCharacterRef>
        {
            RosterRef(WAccount, WCharacter, WContentId),
            RosterRef(XAccount, XCharacter, XContentId),
        };

        for (var index = 3; index <= partySize; index++)
        {
            roster.Add(RosterRef(
                $"account-{index}",
                $"Character {index}@Alpha",
                (ulong)(index * 1000 + index)));
        }

        return roster.Take(partySize).ToList();
    }

    private static DadRosterCharacterRef RosterRef(string account, string character, ulong contentId)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            CharacterKey = new DadCharacterKey(character),
            ContentId = contentId,
        };

    private static DadParticipantSnapshot Participant(
        string account,
        string character,
        ulong contentId,
        string workerSession,
        bool postArReady = true)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(workerSession),
            ManagedAccountKey = new DadAccountKey(account),
            ActiveCharacterKey = new DadCharacterKey(character),
            Character = new DadAcquiredCharacter
            {
                AccountId = account,
                CharacterKey = character,
                ContentId = contentId,
                Source = DadCharacterSource.PeerRuntime,
                Freshness = DadSnapshotFreshness.Live,
                Readiness = DadReadinessState.Ready,
            },
            IsAvailable = true,
            IsEligibleForRun = true,
            PostArReady = postArReady,
            State = DadParticipantState.Ready,
        };
}
