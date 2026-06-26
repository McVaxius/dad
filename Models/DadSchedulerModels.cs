namespace dad.Models;

public enum DadSchedulerWakePolicy
{
    AlreadyOnlineOnly,
    LaunchIfOffline,
    LoadCharacterIfOnline,
}

public enum DadSchedulerPresetPhase
{
    Idle,
    Resolving,
    LaunchingClients,
    WaitingForHeartbeat,
    LoadingCharacters,
    ReadyToStart,
    StartingPlanner,
    StartedPlanner,
    Completed,
    Blocked,
    TimedOut,
    Cancelled,
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
            ExpectedCharacterKeys = [..ExpectedCharacterKeys],
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
    public DadSchedulerWakePolicy WakePolicy { get; set; } = DadSchedulerWakePolicy.AlreadyOnlineOnly;
    public DadAccountKey RequiredAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey RequiredCharacterKey { get; set; } = new(string.Empty);
    public string LaunchProfileId { get; set; } = string.Empty;
    public string LaunchProfileName { get; set; } = string.Empty;
    public string BatchPath { get; set; } = string.Empty;
    public bool LaunchProfileDryRun { get; set; }
    public bool LaunchStarted { get; set; }
    public DateTime? LaunchStartedUtc { get; set; }
    public DateTime? LoadCommandSentUtc { get; set; }
    public bool IsOnline { get; set; }
    public bool CorrectCharacter { get; set; }
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
            LaunchProfileId = LaunchProfileId,
            LaunchProfileName = LaunchProfileName,
            BatchPath = BatchPath,
            LaunchProfileDryRun = LaunchProfileDryRun,
            LaunchStarted = LaunchStarted,
            LaunchStartedUtc = LaunchStartedUtc,
            LoadCommandSentUtc = LoadCommandSentUtc,
            IsOnline = IsOnline,
            CorrectCharacter = CorrectCharacter,
            Ready = Ready,
            RosterVisibility = RosterVisibility,
            NeedsRosterUpdate = NeedsRosterUpdate,
            MatchedWorkerSessionId = MatchedWorkerSessionId,
            ActiveCharacterKey = ActiveCharacterKey,
            Summary = Summary,
            BlockedReason = BlockedReason,
            Warnings = [..Warnings],
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

    public bool IsActive => Phase is DadSchedulerPresetPhase.Resolving
        or DadSchedulerPresetPhase.LaunchingClients
        or DadSchedulerPresetPhase.WaitingForHeartbeat
        or DadSchedulerPresetPhase.LoadingCharacters
        or DadSchedulerPresetPhase.ReadyToStart
        or DadSchedulerPresetPhase.StartingPlanner;

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
        };

    public DadRunResult ToRunResult(DadRunRequest? request = null)
        => new()
        {
            RequestId = string.IsNullOrWhiteSpace(PlannerRequestId) ? SchedulerRunId : PlannerRequestId,
            Status = Phase is DadSchedulerPresetPhase.Blocked or DadSchedulerPresetPhase.TimedOut
                ? DadRunStatus.Rejected
                : Phase == DadSchedulerPresetPhase.Completed
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
