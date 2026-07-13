using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadWorkerCommandValidationRulesTests
{
    private const string RunId = "run-sastasha";
    private const string WAccount = "account-w";
    private const string XAccount = "account-x";
    private const string WCharacter = "W Character@Alpha";
    private const string XCharacter = "X Character@Alpha";
    private const string WWorker = "worker-w";
    private const string XWorker = "worker-x";
    private const ulong WContentId = 1001;
    private const ulong XContentId = 2002;

    [Fact]
    public void ExactSastashaCommandRoundTripsWithoutMogtomePayload()
    {
        var command = BuildCommand();

        var json = DadIpcJson.Serialize(command);
        var roundTrip = DadIpcJson.Deserialize<DadWorkerExecutionCommand>(json);

        Assert.NotNull(roundTrip);
        Assert.Contains("\"mogtome\":null", json, StringComparison.Ordinal);
        Assert.Equal(DadModuleId.PremadeDuty, roundTrip.Plan.CompositeModuleId);
        Assert.Equal(DadModuleId.PremadeDuty, roundTrip.Plan.Modules[roundTrip.ModuleIndex].ModuleId);
        Assert.Equal(2, roundTrip.Plan.Modules[roundTrip.ModuleIndex].ExpectedPartySize);
        Assert.Equal(2, roundTrip.Plan.RequiredParticipantCount);
        Assert.Equal(2, roundTrip.Plan.Orchestration.RosterIntent.ExpectedPartySize);
        Assert.Null(roundTrip.Plan.Request.Mogtome);
        var premade = Assert.IsType<DadPremadeDutyTask>(roundTrip.Plan.Request.PremadeDuty);
        Assert.Equal((uint)4, premade.ContentFinderConditionId);
        Assert.Equal("Sastasha", premade.DutyName);
        Assert.True(premade.Unsynced);
        Assert.Equal(2, premade.ExpectedPartySize);

        var w = Assert.Single(roundTrip.Participants, participant => participant.WorkerSessionId.Value == WWorker);
        Assert.Equal("Slot1", w.AssignedSlotId);
        Assert.Equal(WAccount, w.ManagedAccountKey.Value);
        Assert.Equal(WCharacter, w.ActiveCharacterKey.Value);
        Assert.Equal(WContentId, w.Character.ContentId);

        var x = Assert.Single(roundTrip.Participants, participant => participant.WorkerSessionId.Value == XWorker);
        Assert.True(x.IsLocalClient);
        Assert.Equal("Slot2", x.AssignedSlotId);
        Assert.Equal(XAccount, x.ManagedAccountKey.Value);
        Assert.Equal(XCharacter, x.ActiveCharacterKey.Value);
        Assert.Equal(XContentId, x.Character.ContentId);

        Assert.True(
            DadWorkerCommandValidationRules.TryValidate(roundTrip, RuntimeForX(), out var localAssignment, out var blocker),
            blocker);
        Assert.Equal("Slot2", localAssignment.AssignedSlotId);
    }

    [Fact]
    public void ReversedParticipantListPreservesFrozenSlotAssignments()
    {
        var command = BuildCommand();
        command.Participants.Reverse();

        var valid = DadWorkerCommandValidationRules.TryValidate(
            command,
            RuntimeForX(),
            out var localAssignment,
            out var blocker);

        Assert.True(valid, blocker);
        Assert.Equal(XWorker, command.Participants[0].WorkerSessionId.Value);
        Assert.Equal("Slot2", localAssignment.AssignedSlotId);
        Assert.Equal(XCharacter, localAssignment.ActiveCharacterKey.Value);
    }

    [Fact]
    public void SwappedSlotAssignmentsAreRejected()
    {
        var command = BuildCommand();
        command.Participants[0].AssignedSlotId = "Slot2";
        command.Participants[1].AssignedSlotId = "Slot1";

        AssertRejected(command, RuntimeForX(), "Slot1 assignment identity");
    }

    [Fact]
    public void DuplicateSlotAssignmentsAreRejected()
    {
        var command = BuildCommand();
        command.Participants[1].AssignedSlotId = "Slot1";

        AssertRejected(command, RuntimeForX(), "duplicated assigned slot");
    }

    [Fact]
    public void MissingSlotAssignmentIsRejected()
    {
        var command = BuildCommand();
        command.Participants[1].AssignedSlotId = string.Empty;

        AssertRejected(command, RuntimeForX(), "missing or duplicated assigned slot");
    }

    [Fact]
    public void DuplicateWorkerSessionAssignmentsAreRejected()
    {
        var command = BuildCommand();
        command.Participants[0].WorkerSessionId = new DadWorkerSessionId(XWorker);

        AssertRejected(command, RuntimeForX(), "duplicated worker session");
    }

    [Fact]
    public void WrongWorkerAssignmentIsRejected()
    {
        var runtime = RuntimeForX();
        runtime.WorkerSessionId = new DadWorkerSessionId("worker-y");

        AssertRejected(BuildCommand(), runtime, "another worker/account/character/slot");
    }

    [Fact]
    public void WrongAccountAssignmentIsRejected()
    {
        var runtime = RuntimeForX();
        runtime.ManagedAccountKey = new DadAccountKey("account-y");

        AssertRejected(BuildCommand(), runtime, "another worker/account/character/slot");
    }

    [Fact]
    public void WrongCharacterAssignmentIsRejected()
    {
        var runtime = RuntimeForX();
        runtime.ActiveCharacterKey = new DadCharacterKey("Other Character@Alpha");
        runtime.Character.CharacterKey = "Other Character@Alpha";

        AssertRejected(BuildCommand(), runtime, "another worker/account/character/slot");
    }

    [Fact]
    public void WrongContentIdAssignmentIsRejected()
    {
        var runtime = RuntimeForX();
        runtime.Character.ContentId = 9999;

        AssertRejected(BuildCommand(), runtime, "another worker/account/character/slot");
    }

    [Fact]
    public void StaleRuntimeAssignmentIsRejected()
    {
        var runtime = RuntimeForX();
        runtime.State = DadParticipantState.Stale;

        AssertRejected(BuildCommand(), runtime, "unavailable or stale");
    }

    [Fact]
    public void PostArReadinessLossIsRejected()
    {
        var runtime = RuntimeForX();
        runtime.PostArReady = false;

        AssertRejected(BuildCommand(), runtime, "not post-AR ready");
    }

    [Theory]
    [InlineData(DadRequestedJobPreparationStatus.AlreadyMatched)]
    [InlineData(DadRequestedJobPreparationStatus.Switched)]
    [InlineData(DadRequestedJobPreparationStatus.SoftFailed)]
    public void ExactTerminalRequestedJobProofAuthorizesWorkerCommand(
        DadRequestedJobPreparationStatus status)
    {
        var command = BuildCommandWithRequestedJob();
        var runtime = RuntimeForX();
        var currentJobId = status == DadRequestedJobPreparationStatus.SoftFailed ? 19u : 21u;
        AddRequestedJobProof(command.Participants[1], status, currentJobId);
        AddRequestedJobProof(runtime, status, currentJobId);

        Assert.True(
            DadWorkerCommandValidationRules.TryValidate(command, runtime, out _, out var blocker),
            blocker);
    }

    [Theory]
    [InlineData(DadRequestedJobPreparationStatus.Pending)]
    [InlineData(DadRequestedJobPreparationStatus.AwaitingVerification)]
    [InlineData(DadRequestedJobPreparationStatus.Cancelled)]
    public void NonTerminalRequestedJobProofCannotAuthorizeWorkerCommand(
        DadRequestedJobPreparationStatus status)
    {
        var command = BuildCommandWithRequestedJob();
        var runtime = RuntimeForX();
        AddRequestedJobProof(command.Participants[1], DadRequestedJobPreparationStatus.AlreadyMatched, 21);
        AddRequestedJobProof(runtime, status, 21);

        AssertRejected(command, runtime, "terminal requested-job preparation proof");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("worker")]
    [InlineData("slot")]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("content")]
    [InlineData("job")]
    public void WrongRequestedJobProofIdentityCannotAuthorizeWorkerCommand(string drift)
    {
        var command = BuildCommandWithRequestedJob();
        var runtime = RuntimeForX();
        AddRequestedJobProof(command.Participants[1], DadRequestedJobPreparationStatus.AlreadyMatched, 21);
        AddRequestedJobProof(runtime, DadRequestedJobPreparationStatus.AlreadyMatched, 21);
        var key = runtime.RequestedJobPreparation!.Key;
        runtime.RequestedJobPreparation.Key = drift switch
        {
            "run" => key with { RunId = "other-run" },
            "worker" => key with { WorkerSessionId = new DadWorkerSessionId("other-worker") },
            "slot" => key with { SlotId = "Slot1" },
            "account" => key with { AccountKey = new DadAccountKey("other-account") },
            "character" => key with { CharacterKey = new DadCharacterKey("Other Character@Alpha") },
            "content" => key with { ContentId = 9999 },
            "job" => key with { RequiredJobId = 24 },
            _ => key,
        };

        AssertRejected(command, runtime, "terminal requested-job preparation proof");
    }

    [Fact]
    public void SwitchedProofCannotHideLiveJobDrift()
    {
        var command = BuildCommandWithRequestedJob();
        var runtime = RuntimeForX();
        AddRequestedJobProof(command.Participants[1], DadRequestedJobPreparationStatus.Switched, 21);
        AddRequestedJobProof(runtime, DadRequestedJobPreparationStatus.Switched, 19);

        AssertRejected(command, runtime, "terminal requested-job preparation proof");
    }

    private static void AssertRejected(
        DadWorkerExecutionCommand command,
        DadParticipantSnapshot runtime,
        string expectedBlocker)
    {
        var valid = DadWorkerCommandValidationRules.TryValidate(
            command,
            runtime,
            out _,
            out var blocker);

        Assert.False(valid);
        Assert.Contains(expectedBlocker, blocker, StringComparison.OrdinalIgnoreCase);
    }

    private static DadWorkerExecutionCommand BuildCommand()
    {
        var orchestration = new DadOrchestrationIntent
        {
            AuthorityMode = DadAuthorityMode.ServerDad,
            ModuleTarget = DadModuleId.PremadeDuty,
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            PreferredLeaderCharacterKey = new DadCharacterKey(WCharacter),
            PreferredInviterCharacterKey = new DadCharacterKey(WCharacter),
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = 2,
                RequireRemoteParticipants = true,
                AllowStoredXadbFallback = false,
                RequireExactCharacters = true,
            },
            RequiredRosterCharacters =
            [
                RosterReference(WAccount, WCharacter, WContentId),
                RosterReference(XAccount, XCharacter, XContentId),
            ],
            RequiredAccountKeys = [new DadAccountKey(WAccount), new DadAccountKey(XAccount)],
            RequiredCharacterKeys = [new DadCharacterKey(WCharacter), new DadCharacterKey(XCharacter)],
        };
        var request = new DadRunRequest
        {
            RequestId = RunId,
            RequestedBy = "worker-command-tests",
            Orchestration = orchestration,
            PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = 4,
                DutyName = "Sastasha",
                Unsynced = true,
                ExpectedPartySize = 2,
                Attempts = 1,
            },
        };
        var module = new DadPlannedModuleExecution
        {
            ModuleId = DadModuleId.PremadeDuty,
            DisplayName = "Premade Duty",
            OwnerLabel = "DAD",
            ExpectedPartySize = 2,
            RequiresPeers = true,
            Summary = "Premade Sastasha #4",
        };
        var plan = new DadRunPlan
        {
            Request = request,
            CompositeModuleId = DadModuleId.PremadeDuty,
            Orchestration = orchestration,
            Summary = module.Summary,
            RequiredParticipantCount = 2,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = WCharacter,
            InviterCharacterKey = WCharacter,
            Modules = [module],
        };

        return new DadWorkerExecutionCommand
        {
            CommandId = "command-x-sastasha",
            RunId = RunId,
            ModuleIndex = 0,
            Role = DadWorkerExecutionRole.Participant,
            Plan = plan,
            Participants =
            [
                Assignment(WWorker, WAccount, WCharacter, WContentId, "Slot1", isLocal: false, isAuthority: true),
                Assignment(XWorker, XAccount, XCharacter, XContentId, "Slot2", isLocal: true, isAuthority: false),
            ],
        };
    }

    private static DadWorkerExecutionCommand BuildCommandWithRequestedJob()
    {
        var command = BuildCommand();
        command.Plan.Orchestration.RequiredRosterCharacters[1].RequiredJobId = 21;
        return command;
    }

    private static void AddRequestedJobProof(
        DadParticipantSnapshot participant,
        DadRequestedJobPreparationStatus status,
        uint currentJobId)
    {
        participant.Character.CurrentJobId = currentJobId;
        participant.RequestedJobPreparation = new DadRequestedJobPreparationProof
        {
            Key = new DadRequestedJobPreparationKey(
                RunId,
                new DadWorkerSessionId(XWorker),
                "Slot2",
                new DadAccountKey(XAccount),
                new DadCharacterKey(XCharacter),
                XContentId,
                21),
            Status = status,
            UpdatedAtUtc = DateTime.UtcNow,
            Summary = status.ToString(),
        };
    }

    private static DadParticipantSnapshot RuntimeForX()
        => Assignment(XWorker, XAccount, XCharacter, XContentId, "Slot2", isLocal: true, isAuthority: false);

    private static DadRosterCharacterRef RosterReference(string account, string character, ulong contentId)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            CharacterKey = new DadCharacterKey(character),
            ContentId = contentId,
        };

    private static DadParticipantSnapshot Assignment(
        string worker,
        string account,
        string character,
        ulong contentId,
        string slot,
        bool isLocal,
        bool isAuthority)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(account),
            ActiveCharacterKey = new DadCharacterKey(character),
            Character = new DadAcquiredCharacter
            {
                AccountId = account,
                CharacterKey = character,
                ContentId = contentId,
                Source = isLocal ? DadCharacterSource.LocalRuntime : DadCharacterSource.PeerRuntime,
                Freshness = DadSnapshotFreshness.Live,
                Readiness = DadReadinessState.Ready,
            },
            AssignedSlotId = slot,
            IsLocalClient = isLocal,
            IsAuthority = isAuthority,
            IsAvailable = true,
            IsEligibleForRun = true,
            PostArReady = true,
            State = DadParticipantState.Ready,
        };
}
