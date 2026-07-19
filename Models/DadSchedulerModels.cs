using System.Text.Json.Serialization;

namespace dad.Models;

public enum DadSchedulerWakePolicy
{
    AlreadyOnlineOnly = 0,
    LaunchIfOffline = 1,
    LoadCharacterIfOnline = 2,
}

public enum DadSchedulerPresetPhase
{
    Idle = 0,
    Resolving = 1,
    LaunchingClients = 2,
    WaitingForHeartbeat = 3,
    LoadingCharacters = 4,
    ReadyToStart = 5,
    StartingPlanner = 6,
    StartedPlanner = 7,
    Completed = 8,
    Blocked = 9,
    TimedOut = 10,
    Cancelled = 11,
    Skipped = 12,
    WaitingForDependencies = 13,
    DailyRewardPreflight = 14,
    LevelingBetweenChildren = 15,
    WaitingForAutoPartyAuthorization = 16,
}

public enum DadSchedulerSkipKind
{
    None = 0,
    LevelSeek = 1,
    DailyRouletteReward = 2,
}

public sealed class DadLaunchProfile
{
    public int SchemaVersion { get; set; } = 2;
    public long Revision { get; set; } = 1;
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Launch Profile";
    public string BatchPath { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public List<DadCharacterKey> ExpectedCharacterKeys { get; set; } = [];
    public bool Enabled { get; set; }
    public bool AllowAutoStart { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public bool DryRun { get; set; } = true;

    public DadLaunchProfile Normalize()
    {
        SchemaVersion = Math.Max(2, SchemaVersion);
        Revision = Math.Max(1, Revision);
        ProfileId = string.IsNullOrWhiteSpace(ProfileId) ? Guid.NewGuid().ToString("N") : ProfileId.Trim();
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "Launch Profile" : DisplayName.Trim();
        BatchPath = BatchPath?.Trim() ?? string.Empty;
        AccountKey = new DadAccountKey((AccountKey.Value ?? string.Empty).Trim());
        ExpectedCharacterKeys = ExpectedCharacterKeys
            .Where(static key => !key.IsEmpty)
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        TimeoutSeconds = Math.Clamp(TimeoutSeconds <= 0 ? 300 : TimeoutSeconds, 30, 1800);
        return this;
    }

    public DadLaunchProfile Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            Revision = Revision,
            ProfileId = ProfileId,
            DisplayName = DisplayName,
            BatchPath = BatchPath,
            AccountKey = AccountKey,
            ExpectedCharacterKeys = [.. ExpectedCharacterKeys],
            Enabled = Enabled,
            AllowAutoStart = AllowAutoStart,
            TimeoutSeconds = TimeoutSeconds,
            DryRun = DryRun,
        };
}

public sealed class DadCharacterLoadInstruction
{
    public bool Enabled { get; set; }
    public string CommandTemplate { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 180;
    public bool DryRun { get; set; } = true;

    public DadCharacterLoadInstruction Normalize()
    {
        CommandTemplate = CommandTemplate?.Trim() ?? string.Empty;
        TimeoutSeconds = Math.Clamp(TimeoutSeconds <= 0 ? 180 : TimeoutSeconds, 30, 1800);
        return this;
    }

    public DadCharacterLoadInstruction Clone()
        => new()
        {
            Enabled = Enabled,
            CommandTemplate = CommandTemplate,
            TimeoutSeconds = TimeoutSeconds,
            DryRun = DryRun,
        };

    public string BuildCommand(DadCharacterKey characterKey, DadAccountKey accountKey)
    {
        Normalize();
        if (!Enabled || string.IsNullOrWhiteSpace(CommandTemplate) || characterKey.IsEmpty)
            return string.Empty;

        var character = characterKey.Value.Trim();
        var split = character.Split('@', 2, StringSplitOptions.TrimEntries);
        var characterName = split.Length > 0 ? split[0] : character;
        var worldName = split.Length > 1 ? split[1] : string.Empty;

        return CommandTemplate
            .Replace("{Character}", character, StringComparison.OrdinalIgnoreCase)
            .Replace("{CharacterKey}", character, StringComparison.OrdinalIgnoreCase)
            .Replace("{CharacterName}", characterName, StringComparison.OrdinalIgnoreCase)
            .Replace("{World}", worldName, StringComparison.OrdinalIgnoreCase)
            .Replace("{WorldName}", worldName, StringComparison.OrdinalIgnoreCase)
            .Replace("{Account}", accountKey.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{AccountKey}", accountKey.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DadCharacterLoadCommandDto
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public string Command { get; set; } = string.Empty;
    public bool DryRun { get; set; } = true;
}

public sealed class DadCharacterLoadResultDto
{
    public string CommandId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool DryRun { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadParticipantSnapshot Snapshot { get; set; } = new();
}

public sealed class DadSchedulerSlotState
{
    public string SlotId { get; set; } = string.Empty;
    public DadSchedulerWakePolicy WakePolicy { get; set; } = DadSchedulerWakePolicy.LaunchIfOffline;
    public DadAccountKey RequiredAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey RequiredCharacterKey { get; set; } = new(string.Empty);
    public uint? RequiredJobId { get; set; }
    public DadAdsLootMode AdsLootMode { get; set; } = DadAdsLootMode.NoChange;
    public int? LevelSeekTarget { get; set; }
    public string LaunchProfileId { get; set; } = string.Empty;
    public string LaunchProfileName { get; set; } = string.Empty;
    public string BatchPath { get; set; } = string.Empty;
    public bool LaunchProfileDryRun { get; set; }
    public bool LaunchStarted { get; set; }
    public DateTime? LaunchStartedUtc { get; set; }
    public DateTime? LoadCommandSentUtc { get; set; }
    public DadWakeTakeoverStatus TakeoverStatus { get; set; } = DadWakeTakeoverStatus.Pending;
    public DadWakeTakeoverStage TakeoverStage { get; set; } = DadWakeTakeoverStage.None;
    public DadWakeTakeoverPhase TakeoverPhase { get; set; } = DadWakeTakeoverPhase.AwaitingArHook;
    public string OperationToken { get; set; } = string.Empty;
    public DadWakeCommitKind CommitKind { get; set; }
    public DateTime? CommitExecutionUtc { get; set; }
    public DadWakeAcknowledgementState AcknowledgementState { get; set; }
    public DateTime? TakeoverRequestedUtc { get; set; }
    public DateTime? ResetIssuedUtc { get; set; }
    public DateTime? ResetExecutionUtc { get; set; }
    public DateTime? TakeoverVerifiedUtc { get; set; }
    public DateTime? RelogIssuedUtc { get; set; }
    public DateTime? RelogExecutionUtc { get; set; }
    public DateTime? ReadyUtc { get; set; }
    public bool PostArReady { get; set; }
    [JsonIgnore]
    public bool BasePostArReady { get; set; }
    public bool AutoRetainerAvailable { get; set; }
    public bool AutoRetainerBusy { get; set; }
    public bool MultiModeEnabled { get; set; }
    public bool RelogIssued { get; set; }
    public bool ExternalAutomationHeld { get; set; }
    public string ExternalAutomationActivity { get; set; } = string.Empty;
    public string ExternalAutomationState { get; set; } = string.Empty;
    public string ExternalAutomationSummary { get; set; } = string.Empty;
    public DadVermaxionReservationState VermaxionReservationState { get; set; } = DadVermaxionReservationState.NotLoaded;
    public string VermaxionReservationSummary { get; set; } = string.Empty;
    public DateTime? VermaxionReservationCreatedAtUtc { get; set; }
    public DateTime? VermaxionReservationUpdatedAtUtc { get; set; }
    public DateTime? NextTakeoverStatusCheckUtc { get; set; }
    public DateTime? VermaxionHoldStartedUtc { get; set; }
    public DateTime? AutoRetainerWaitStartedUtc { get; set; }
    public DateTime? ParticipantWaitStartedUtc { get; set; }
    public DadWakeTimeoutStage TimeoutStage { get; set; }
    public DateTime? TimeoutStageObservedUtc { get; set; }
    public double VermaxionHoldElapsedSeconds { get; set; }
    public double AutoRetainerWaitElapsedSeconds { get; set; }
    public double ParticipantWaitElapsedSeconds { get; set; }
    public bool ClientConnected { get; set; }
    public bool IsOnline { get; set; }
    public bool CorrectCharacter { get; set; }
    public bool DependenciesReady { get; set; }
    public DadDependencyState DependencyState { get; set; } = DadDependencyState.Checking;
    public long DependencyRevision { get; set; }
    public string DependencySummary { get; set; } = "Checking required plugins.";
    public bool Ready { get; set; }
    public DadRosterVisibility RosterVisibility { get; set; } = DadRosterVisibility.Active;
    public bool NeedsRosterUpdate { get; set; }
    public DadWorkerSessionId MatchedWorkerSessionId { get; set; } = new(string.Empty);
    public DadCharacterKey ActiveCharacterKey { get; set; } = new(string.Empty);
    public string Summary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];

    public DadSchedulerSlotState Clone()
        => new()
        {
            SlotId = SlotId,
            WakePolicy = WakePolicy,
            RequiredAccountKey = RequiredAccountKey,
            RequiredCharacterKey = RequiredCharacterKey,
            RequiredJobId = RequiredJobId,
            AdsLootMode = AdsLootMode,
            LevelSeekTarget = LevelSeekTarget,
            LaunchProfileId = LaunchProfileId,
            LaunchProfileName = LaunchProfileName,
            BatchPath = BatchPath,
            LaunchProfileDryRun = LaunchProfileDryRun,
            LaunchStarted = LaunchStarted,
            LaunchStartedUtc = LaunchStartedUtc,
            LoadCommandSentUtc = LoadCommandSentUtc,
            TakeoverStatus = TakeoverStatus,
            TakeoverStage = TakeoverStage,
            TakeoverPhase = TakeoverPhase,
            OperationToken = OperationToken,
            CommitKind = CommitKind,
            CommitExecutionUtc = CommitExecutionUtc,
            AcknowledgementState = AcknowledgementState,
            TakeoverRequestedUtc = TakeoverRequestedUtc,
            ResetIssuedUtc = ResetIssuedUtc,
            ResetExecutionUtc = ResetExecutionUtc,
            TakeoverVerifiedUtc = TakeoverVerifiedUtc,
            RelogIssuedUtc = RelogIssuedUtc,
            RelogExecutionUtc = RelogExecutionUtc,
            ReadyUtc = ReadyUtc,
            PostArReady = PostArReady,
            BasePostArReady = BasePostArReady,
            AutoRetainerAvailable = AutoRetainerAvailable,
            AutoRetainerBusy = AutoRetainerBusy,
            MultiModeEnabled = MultiModeEnabled,
            RelogIssued = RelogIssued,
            ExternalAutomationHeld = ExternalAutomationHeld,
            ExternalAutomationActivity = ExternalAutomationActivity,
            ExternalAutomationState = ExternalAutomationState,
            ExternalAutomationSummary = ExternalAutomationSummary,
            VermaxionReservationState = VermaxionReservationState,
            VermaxionReservationSummary = VermaxionReservationSummary,
            VermaxionReservationCreatedAtUtc = VermaxionReservationCreatedAtUtc,
            VermaxionReservationUpdatedAtUtc = VermaxionReservationUpdatedAtUtc,
            NextTakeoverStatusCheckUtc = NextTakeoverStatusCheckUtc,
            VermaxionHoldStartedUtc = VermaxionHoldStartedUtc,
            AutoRetainerWaitStartedUtc = AutoRetainerWaitStartedUtc,
            ParticipantWaitStartedUtc = ParticipantWaitStartedUtc,
            TimeoutStage = TimeoutStage,
            TimeoutStageObservedUtc = TimeoutStageObservedUtc,
            VermaxionHoldElapsedSeconds = VermaxionHoldElapsedSeconds,
            AutoRetainerWaitElapsedSeconds = AutoRetainerWaitElapsedSeconds,
            ParticipantWaitElapsedSeconds = ParticipantWaitElapsedSeconds,
            ClientConnected = ClientConnected,
            IsOnline = IsOnline,
            CorrectCharacter = CorrectCharacter,
            DependenciesReady = DependenciesReady,
            DependencyState = DependencyState,
            DependencyRevision = DependencyRevision,
            DependencySummary = DependencySummary,
            Ready = Ready,
            RosterVisibility = RosterVisibility,
            NeedsRosterUpdate = NeedsRosterUpdate,
            MatchedWorkerSessionId = MatchedWorkerSessionId,
            ActiveCharacterKey = ActiveCharacterKey,
            Summary = Summary,
            BlockedReason = BlockedReason,
            Warnings = [.. Warnings],
        };
}

public sealed class DadSchedulerPreview
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string GroupId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public DadSchedulerPresetPhase Phase { get; set; } = DadSchedulerPresetPhase.Idle;
    public bool CanStart { get; set; }
    public bool ReadyToStart { get; set; }
    public string StatusSummary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public List<DadSchedulerSlotState> Slots { get; set; } = [];
    public List<DadLaunchProfile> LaunchProfiles { get; set; } = [];
    public DadPlannerRunRequestPreview PlannerRequestPreview { get; set; } = new();
}

public sealed class DadSchedulerPresetState
{
    public string SchedulerRunId { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = string.Empty;
    public DadSchedulerJobType JobType { get; set; } = DadSchedulerJobType.ScheduledPreset;
    public string RequestedBy { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public DadSchedulerPresetPhase Phase { get; set; } = DadSchedulerPresetPhase.Idle;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? ResetExecutionUtc { get; set; }
    public DateTime? RelogExecutionUtc { get; set; }
    public bool DryRun { get; set; }
    public bool PlannerStarted { get; set; }
    public string PlannerRequestId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public List<DadSchedulerSlotState> Slots { get; set; } = [];
    public string ScheduleId { get; set; } = string.Empty;
    public string ScheduleRunId { get; set; } = string.Empty;
    public string ScheduleEntryId { get; set; } = string.Empty;
    public int ScheduleEntryIndex { get; set; } = -1;
    public int ScheduleRepeatIteration { get; set; }
    public DadScheduleCadence ScheduleCadence { get; set; } = DadScheduleCadence.Manual;
    public DadSchedulerSkipKind SkipKind { get; set; }
    public string ParentOperationJobId { get; set; } = string.Empty;
    public int LevelingIteration { get; set; }

    public bool IsActive => Phase is DadSchedulerPresetPhase.Resolving
        or DadSchedulerPresetPhase.LaunchingClients
        or DadSchedulerPresetPhase.WaitingForHeartbeat
        or DadSchedulerPresetPhase.LoadingCharacters
        or DadSchedulerPresetPhase.DailyRewardPreflight
        or DadSchedulerPresetPhase.WaitingForDependencies
        or DadSchedulerPresetPhase.ReadyToStart
        or DadSchedulerPresetPhase.StartingPlanner
        or DadSchedulerPresetPhase.LevelingBetweenChildren
        or DadSchedulerPresetPhase.WaitingForAutoPartyAuthorization;

    public DadSchedulerPresetState Clone()
        => new()
        {
            SchedulerRunId = SchedulerRunId,
            JobId = JobId,
            JobType = JobType,
            RequestedBy = RequestedBy,
            GroupId = GroupId,
            PresetName = PresetName,
            Phase = Phase,
            StartedAtUtc = StartedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            CompletedAtUtc = CompletedAtUtc,
            ResetExecutionUtc = ResetExecutionUtc,
            RelogExecutionUtc = RelogExecutionUtc,
            DryRun = DryRun,
            PlannerStarted = PlannerStarted,
            PlannerRequestId = PlannerRequestId,
            Summary = Summary,
            BlockedReason = BlockedReason,
            Slots = Slots.Select(static slot => slot.Clone()).ToList(),
            ScheduleId = ScheduleId,
            ScheduleRunId = ScheduleRunId,
            ScheduleEntryId = ScheduleEntryId,
            ScheduleEntryIndex = ScheduleEntryIndex,
            ScheduleRepeatIteration = ScheduleRepeatIteration,
            ScheduleCadence = ScheduleCadence,
            SkipKind = SkipKind,
            ParentOperationJobId = ParentOperationJobId,
            LevelingIteration = LevelingIteration,
        };

    public DadRunResult ToRunResult(DadRunRequest? request = null)
        => new()
        {
            RequestId = string.IsNullOrWhiteSpace(PlannerRequestId) ? SchedulerRunId : PlannerRequestId,
            Status = Phase == DadSchedulerPresetPhase.Cancelled
                ? DadRunStatus.Cancelled
                : Phase is DadSchedulerPresetPhase.Blocked or DadSchedulerPresetPhase.TimedOut
                ? DadRunStatus.Rejected
                : Phase is DadSchedulerPresetPhase.Completed or DadSchedulerPresetPhase.Skipped
                    ? DadRunStatus.Completed
                : Phase == DadSchedulerPresetPhase.StartedPlanner
                    ? DadRunStatus.Queued
                    : DadRunStatus.Running,
            Phase = DadRunPhase.DiscoveringParticipants,
            ModuleId = request?.Orchestration?.ModuleTarget ?? DadModuleId.Mixed,
            RequestedBy = string.IsNullOrWhiteSpace(RequestedBy) ? "scheduler" : RequestedBy,
            Summary = Summary,
            BlockedReason = BlockedReason,
            Request = request,
            ActiveTaskName = "Scheduler",
            ActiveTaskStatus = Summary,
            ActiveTaskIndex = Slots.Count(static slot => slot.Ready),
            TotalTaskCount = Math.Max(1, Slots.Count),
            CompletedAtUtc = CompletedAtUtc,
            Warnings = Slots
                .SelectMany(static slot => slot.Warnings)
                .Where(static warning => !string.IsNullOrWhiteSpace(warning))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
}

public sealed class DadSchedulerStartRequest
{
    public string GroupId { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
}
