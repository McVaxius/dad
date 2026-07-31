using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadSchedulerService
{
    private string ClientBootDirectory => configuration.ClientBootDirectory;
    private const int DefaultScheduleCadenceHours = 18;
    private const int MaxSchedulerHistory = 50;
    private const int MaxScheduleHistory = 50;
    private const int ScheduleJobPriority = 1000;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TakeoverStatusInterval = TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadProfileDirectoryService profileDirectoryService;
    private readonly DadCharacterIntelligenceService characterIntelligenceService;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadWakeTakeoverService wakeTakeoverService;
    private readonly DadRosterCatalogService rosterCatalogService;
    private readonly IPluginLog log;
    private readonly Dictionary<string, string> takeoverDiagnosticStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingTakeoverCancellation> pendingTakeoverCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingEarlyAssignmentCancellation> pendingEarlyAssignmentCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadStableContradictionTracker> slotContradictionTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly DadImmutableCommandRegistry frozenRequestRegistry = new();
    private DadStrictPlannerRevalidationTracker strictRevalidationTracker = new();
    private DadSchedulerPresetPhase? lastLoggedSchedulerPhase;
    private bool dependencyGateCommitted;
    private DadModuleId activeSchedulerModuleId = DadModuleId.None;
    private DadSchedulerPresetState currentState = new() { Phase = DadSchedulerPresetPhase.Idle, Summary = "Scheduler idle." };
    private DadScheduledCrewJob? activeJob;
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private DateTime suppressAutomaticEnqueueUntilUtc = DateTime.MinValue;
    private DadRunRequest? frozenPlannerRequest;
    private DadPlannerGroup? frozenLevelSeekGroup;
    private bool postWakeLevelTargetsEvaluated;
    private List<FrozenEarlyAssignment> frozenEarlyAssignments = [];
    private List<DadSchedulerSlotState> frozenOrdinarySlots = [];
    private DailyRewardPreflightSession? dailyRewardPreflight;
    private Func<DadPlannerGroup, int, DadLevelingChildBuild>? levelingChildBuilder;
    private Action? cancelActiveLevelingChild;
    private Func<DadRunRequest, DadAutoPartyAuthorizationDecision>? autoPartyAuthorizationGate;
    private Func<DadRunRequest, DadRunResult>? startCrewRegularParty;
    private Func<string, DadPlannerGroup, DadActivityPreset, DadAlliancePartyFinderStatus>? createCrewAllianceParty;
    private Func<DadAlliancePartyFinderStatus>? getCrewAllianceStatus;
    private Func<DadAlliancePartyFinderStatus>? grabCrewAllianceParty;
    private Action<string>? stopCrewAllianceParty;
    private LevelingOperationSession? levelingOperation;
    private CrewFormationSession? crewFormation;

    private sealed class PendingTakeoverCancellation
    {
        public string SchedulerRunId { get; init; } = string.Empty;
        public DadScheduledCrewJob Job { get; init; } = new();
        public DadSchedulerSlotState Slot { get; init; } = new();
        public string Reason { get; init; } = string.Empty;
        public DateTime RequestedAtUtc { get; init; } = DateTime.UtcNow;
        public DateTime NextAttemptUtc { get; set; } = DateTime.MinValue;
        public bool BlocksFutureWork { get; init; } = true;
    }

    private sealed class FrozenEarlyAssignment
    {
        public string CommandId { get; init; } = string.Empty;
        public DadWakeRequestDto Request { get; init; } = new();
        public DadScheduledCrewJob Job { get; init; } = new();
        public HashSet<string> AttemptedWorkerSessionIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AcknowledgedWorkerSessionIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string LastDiagnosticState { get; set; } = string.Empty;
    }

    private sealed class DailyRewardPreflightSession
    {
        public DadQueueTarget Target { get; init; } = new() { Kind = DadQueueTargetKind.Roulette };
        public int CheckedSlotCount { get; init; }
        public int CurrentSlotIndex { get; set; }
        public int ReceivedSlotCount { get; set; }
        public DateTime SlotDeadlineUtc { get; set; }
        public DadRouletteRewardProbeRequestDto? ActiveRequest { get; set; }
        public List<string> Evidence { get; } = [];
    }

    private sealed class LevelingOperationSession
    {
        public DadPlannerGroup SourceGroup { get; init; } = new();
        public DadScheduledCrewJob OuterJob { get; init; } = new();
        public DadSchedulerPresetState OuterState { get; init; } = new();
        public DadLevelingChildBuild ChildBuild { get; set; } = new();
        public int Iteration { get; set; } = 1;
        public bool RefreshingRoster { get; set; }
        public DateTime RefreshDeadlineUtc { get; set; }
        public List<LevelingRosterRefreshRow> RefreshRows { get; set; } = [];
        public HashSet<string> IssuedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CrewFormationSession
    {
        public DadCrewFormationStatus Status { get; init; } = new();
        public DadPlannerGroup EffectiveGroup { get; init; } = new();
        public DadActivityPreset AlliancePreview { get; init; } = new();
        public string SchedulerJobId { get; init; } = string.Empty;
        public string TerminalFailure { get; set; } = string.Empty;
    }

    private sealed class LevelingRosterRefreshRow
    {
        public DadLevelingSlotSelection Selection { get; init; } = new();
        public DadParticipantSnapshot Participant { get; init; } = new();
        public DadRosterRefreshCommandDto Command { get; init; } = new();
        public bool Completed { get; set; }
    }

    private sealed class PendingEarlyAssignmentCancellation
    {
        public string RunId { get; init; } = string.Empty;
        public DadWorkerSessionId WorkerSessionId { get; init; } = new(string.Empty);
        public DadScheduledCrewJob Job { get; init; } = new();
        public string SlotId { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public DateTime NextAttemptUtc { get; set; } = DateTime.MinValue;
        public bool BlocksFutureWork { get; init; } = true;
    }

    public DadSchedulerService(
        Configuration configuration,
        ConfigManager configManager,
        DadProfileDirectoryService profileDirectoryService,
        DadCharacterIntelligenceService characterIntelligenceService,
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadWakeTakeoverService wakeTakeoverService,
        DadRosterCatalogService rosterCatalogService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.profileDirectoryService = profileDirectoryService;
        this.characterIntelligenceService = characterIntelligenceService;
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.wakeTakeoverService = wakeTakeoverService;
        this.rosterCatalogService = rosterCatalogService;
        this.log = log;
        RecoverAbandonedScheduleRun();
    }

    public DadSchedulerPresetState CurrentState => BuildVisibleState();

    private bool IsSchedulerActive
        => levelingOperation != null || currentState.IsActive || IsCrewFormationActive;

    internal bool IsCrewFormationActive => crewFormation?.Status.IsActive == true;

    public void ConfigureLevelingMode(
        Func<DadPlannerGroup, int, DadLevelingChildBuild> childBuilder,
        Action cancelActiveChild)
    {
        levelingChildBuilder = childBuilder ?? throw new ArgumentNullException(nameof(childBuilder));
        cancelActiveLevelingChild = cancelActiveChild ?? throw new ArgumentNullException(nameof(cancelActiveChild));
    }

    public void ConfigureAutoPartyAuthorizationGate(
        Func<DadRunRequest, DadAutoPartyAuthorizationDecision> authorizationGate)
        => autoPartyAuthorizationGate = authorizationGate ?? throw new ArgumentNullException(nameof(authorizationGate));

    internal void ConfigureCrewFormation(
        Func<DadRunRequest, DadRunResult> startRegularParty,
        Func<string, DadPlannerGroup, DadActivityPreset, DadAlliancePartyFinderStatus> createAllianceParty,
        Func<DadAlliancePartyFinderStatus> getAllianceStatus,
        Func<DadAlliancePartyFinderStatus> grabAllianceParty,
        Action<string> stopAllianceParty)
    {
        startCrewRegularParty = startRegularParty ?? throw new ArgumentNullException(nameof(startRegularParty));
        createCrewAllianceParty = createAllianceParty ?? throw new ArgumentNullException(nameof(createAllianceParty));
        getCrewAllianceStatus = getAllianceStatus ?? throw new ArgumentNullException(nameof(getAllianceStatus));
        grabCrewAllianceParty = grabAllianceParty ?? throw new ArgumentNullException(nameof(grabAllianceParty));
        stopCrewAllianceParty = stopAllianceParty ?? throw new ArgumentNullException(nameof(stopAllianceParty));
    }

    internal DadCrewFormationStatus GetCrewFormationStatus()
        => crewFormation?.Status.Clone() ?? new DadCrewFormationStatus();

    internal bool IsActiveCrewFormationRun(string runId)
        => !string.IsNullOrWhiteSpace(runId) &&
           crewFormation?.Status.IsActive == true &&
           string.Equals(crewFormation.Status.RunId, runId, StringComparison.Ordinal);

    internal string GetCrewFormationSchedulerBlocker()
    {
        NormalizeQueue();
        NormalizeSchedules();
        if (IsCrewFormationActive)
            return "Crew Formation is already active.";
        if (IsSchedulerActive)
            return "A scheduler preset is already active.";
        if (configuration.ActiveScheduleRun?.IsActive == true)
            return "A schedule run is already active.";
        if (configuration.SchedulerQueue.Count > 0)
            return "The scheduler queue must be empty before Crew Formation starts.";
        if (HasPendingCleanup)
            return "Scheduler takeover or requested-job cancellation cleanup is still pending.";
        return string.Empty;
    }

    internal DadCrewFormationStatus StartCrewFormation(
        DadPlannerGroup sourceGroup,
        DadPlannerGroup effectiveGroup,
        DadPlannerRunRequestPreview plannerRequestPreview,
        DadActivityPreset alliancePreview,
        DadCrewFormationClassification classification)
    {
        ArgumentNullException.ThrowIfNull(sourceGroup);
        ArgumentNullException.ThrowIfNull(effectiveGroup);
        ArgumentNullException.ThrowIfNull(plannerRequestPreview);
        ArgumentNullException.ThrowIfNull(alliancePreview);
        ArgumentNullException.ThrowIfNull(classification);

        var now = DateTime.UtcNow;
        var status = new DadCrewFormationStatus
        {
            RunId = Guid.NewGuid().ToString("N"),
            RequestId = plannerRequestPreview.Request?.RequestId ?? string.Empty,
            SourceGroupId = sourceGroup.GroupId,
            SourcePresetName = sourceGroup.DisplayName,
            EffectiveGroupId = effectiveGroup.GroupId,
            EffectivePresetName = effectiveGroup.DisplayName,
            Mode = classification.Mode,
            Phase = DadCrewFormationPhase.Preparing,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Summary = $"Preparing {classification.Summary} for preset '{sourceGroup.DisplayName}'.",
        };

        var blocker = FirstNonEmptyOrEmpty(
            classification.BlockedReason,
            GetCrewFormationSchedulerBlocker(),
            plannerRequestPreview.CanSchedule && plannerRequestPreview.Request != null
                ? string.Empty
                : FirstNonEmpty(
                    plannerRequestPreview.BlockedReason,
                    plannerRequestPreview.StatusSummary,
                    "The selected preset is not schedulable."));
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            status.Phase = DadCrewFormationPhase.Blocked;
            status.BlockedReason = blocker;
            status.Summary = blocker;
            status.CompletedAtUtc = now;
            crewFormation = new CrewFormationSession { Status = status };
            return status.Clone();
        }

        if (startCrewRegularParty == null ||
            createCrewAllianceParty == null ||
            getCrewAllianceStatus == null ||
            grabCrewAllianceParty == null ||
            stopCrewAllianceParty == null)
        {
            status.Phase = DadCrewFormationPhase.Blocked;
            status.BlockedReason = "Crew Formation runtime callbacks are unavailable.";
            status.Summary = status.BlockedReason;
            status.CompletedAtUtc = now;
            crewFormation = new CrewFormationSession { Status = status };
            return status.Clone();
        }

        var frozenGroup = CloneCrewValue(effectiveGroup);
        var frozenAlliancePreview = CloneCrewValue(alliancePreview);
        var job = new DadScheduledCrewJob
        {
            JobType = DadSchedulerJobType.ScheduledPreset,
            GroupId = frozenGroup.GroupId,
            PresetName = frozenGroup.DisplayName,
            DryRun = false,
            RequestedBy = "crew-tools",
            CreatedAtUtc = now,
        };
        crewFormation = new CrewFormationSession
        {
            Status = status,
            EffectiveGroup = frozenGroup,
            AlliancePreview = frozenAlliancePreview,
            SchedulerJobId = job.JobId,
        };

        var schedulerState = StartOrdinaryPreset(
            frozenGroup,
            plannerRequestPreview,
            dryRun: false,
            job);
        status.SchedulerRunId = schedulerState.SchedulerRunId;
        status.RequestId = schedulerState.PlannerRequestId;
        status.UpdatedAtUtc = DateTime.UtcNow;
        if (!schedulerState.IsActive)
        {
            FinishCrewFormation(
                schedulerState.Phase == DadSchedulerPresetPhase.Cancelled
                    ? DadCrewFormationPhase.Cancelled
                    : DadCrewFormationPhase.Blocked,
                FirstNonEmpty(schedulerState.BlockedReason, schedulerState.Summary));
        }
        else
        {
            status.Summary = schedulerState.Summary;
        }

        return crewFormation.Status.Clone();
    }

    internal DadSchedulerUiRevision GetPlannerUiRevision()
    {
        var schedulerHash = new HashCode();
        schedulerHash.Add(currentState.SchedulerRunId, StringComparer.Ordinal);
        schedulerHash.Add(currentState.JobId, StringComparer.Ordinal);
        schedulerHash.Add(currentState.Phase);
        schedulerHash.Add(currentState.UpdatedAtUtc.Ticks);
        schedulerHash.Add(currentState.CompletedAtUtc?.Ticks ?? 0);
        schedulerHash.Add(currentState.PlannerStarted);
        schedulerHash.Add(currentState.ScheduleCadence);
        schedulerHash.Add(currentState.SkipKind);
        schedulerHash.Add(crewFormation?.Status.RunId, StringComparer.Ordinal);
        schedulerHash.Add(crewFormation?.Status.Phase);
        schedulerHash.Add(currentState.Slots.Count);
        foreach (var slot in currentState.Slots)
        {
            schedulerHash.Add(slot.SlotId, StringComparer.Ordinal);
            schedulerHash.Add(slot.WakePolicy);
            schedulerHash.Add(slot.LaunchStarted);
            schedulerHash.Add(slot.LoadCommandSentUtc?.Ticks ?? 0);
            schedulerHash.Add(slot.TakeoverStatus);
            schedulerHash.Add(slot.TakeoverStage);
            schedulerHash.Add(slot.TakeoverPhase);
            schedulerHash.Add(slot.OperationToken);
            schedulerHash.Add(slot.CommitKind);
            schedulerHash.Add(slot.CommitExecutionUtc?.Ticks ?? 0);
            schedulerHash.Add(slot.AcknowledgementState);
            schedulerHash.Add(slot.TakeoverRequestedUtc?.Ticks ?? 0);
            schedulerHash.Add(slot.ResetIssuedUtc?.Ticks ?? 0);
            schedulerHash.Add(slot.RelogIssuedUtc?.Ticks ?? 0);
            schedulerHash.Add(slot.PostArReady);
            schedulerHash.Add(slot.AutoRetainerBusy);
            schedulerHash.Add(slot.MultiModeEnabled);
            schedulerHash.Add(slot.RelogIssued);
            schedulerHash.Add(slot.ExternalAutomationHeld);
            schedulerHash.Add(slot.ExternalAutomationActivity, StringComparer.Ordinal);
            schedulerHash.Add(slot.ExternalAutomationState, StringComparer.Ordinal);
            schedulerHash.Add(slot.VermaxionReservationState);
            schedulerHash.Add(slot.VermaxionReservationUpdatedAtUtc?.Ticks ?? 0);
            schedulerHash.Add(slot.NextTakeoverStatusCheckUtc?.Ticks ?? 0);
            schedulerHash.Add(slot.TimeoutStage);
            schedulerHash.Add(slot.ClientConnected);
            schedulerHash.Add(slot.IsOnline);
            schedulerHash.Add(slot.CorrectCharacter);
            schedulerHash.Add(slot.Ready);
            schedulerHash.Add(slot.BlockedReason, StringComparer.Ordinal);
        }

        schedulerHash.Add(configuration.SchedulerQueue?.Count ?? 0);
        foreach (var job in configuration.SchedulerQueue ?? [])
        {
            schedulerHash.Add(job.JobId, StringComparer.Ordinal);
            schedulerHash.Add(job.JobType);
            schedulerHash.Add(job.GroupId, StringComparer.Ordinal);
            schedulerHash.Add(job.Enabled);
            schedulerHash.Add(job.DryRun);
            schedulerHash.Add(job.NextEligibleTimeUtc?.Ticks ?? 0);
            schedulerHash.Add(job.Priority);
            schedulerHash.Add(job.StatusSummary, StringComparer.Ordinal);
            schedulerHash.Add(job.BlockedReason, StringComparer.Ordinal);
            schedulerHash.Add(job.ScheduleRunId, StringComparer.Ordinal);
            schedulerHash.Add(job.ScheduleEntryId, StringComparer.Ordinal);
            schedulerHash.Add(job.ScheduleRepeatIteration);
            schedulerHash.Add(job.ScheduleCadence);
        }

        schedulerHash.Add(configuration.Schedules?.Count ?? 0);
        foreach (var schedule in configuration.Schedules ?? [])
        {
            schedulerHash.Add(schedule.ScheduleId, StringComparer.Ordinal);
            schedulerHash.Add(schedule.DisplayName, StringComparer.Ordinal);
            schedulerHash.Add(schedule.Cadence);
            schedulerHash.Add(schedule.Revision);
            schedulerHash.Add(schedule.LastDailyResetUtc?.Ticks ?? 0);
            schedulerHash.Add(schedule.Entries?.Count ?? 0);
            foreach (var entry in schedule.Entries ?? [])
            {
                schedulerHash.Add(entry.EntryId, StringComparer.Ordinal);
                schedulerHash.Add(entry.GroupId, StringComparer.Ordinal);
                schedulerHash.Add(entry.RepeatCount);
            }
        }

        schedulerHash.Add(configuration.ActiveScheduleRun?.RunId ?? string.Empty, StringComparer.Ordinal);
        schedulerHash.Add(configuration.ActiveScheduleRun?.Status ?? DadScheduleRunStatus.Idle);
        schedulerHash.Add(configuration.ActiveScheduleRun?.Phase ?? DadScheduleRunPhase.Idle);
        schedulerHash.Add(configuration.ActiveScheduleRun?.UpdatedAtUtc.Ticks ?? 0);
        schedulerHash.Add(configuration.ScheduleHistory?.Count ?? 0);
        schedulerHash.Add(pendingTakeoverCancellations.Count);
        foreach (var pending in pendingTakeoverCancellations.OrderBy(static pair => pair.Key))
        {
            schedulerHash.Add(pending.Key, StringComparer.Ordinal);
            schedulerHash.Add(pending.Value.Job.JobId, StringComparer.Ordinal);
            schedulerHash.Add(pending.Value.Job.GroupId, StringComparer.Ordinal);
        }
        schedulerHash.Add(pendingEarlyAssignmentCancellations.Count);
        foreach (var pending in pendingEarlyAssignmentCancellations.OrderBy(static pair => pair.Key))
        {
            schedulerHash.Add(pending.Key, StringComparer.Ordinal);
            schedulerHash.Add(pending.Value.Job.JobId, StringComparer.Ordinal);
        }

        var launchProfilesHash = new HashCode();
        launchProfilesHash.Add(configuration.LaunchProfiles?.Count ?? 0);
        foreach (var profile in configuration.LaunchProfiles ?? [])
        {
            launchProfilesHash.Add(profile.ProfileId, StringComparer.Ordinal);
            launchProfilesHash.Add(profile.DisplayName, StringComparer.Ordinal);
            launchProfilesHash.Add(profile.BatchPath, StringComparer.Ordinal);
            launchProfilesHash.Add(profile.AccountKey.Value, StringComparer.Ordinal);
            launchProfilesHash.Add(profile.Enabled);
            launchProfilesHash.Add(profile.AllowAutoStart);
            launchProfilesHash.Add(profile.TimeoutSeconds);
            launchProfilesHash.Add(profile.DryRun);
            foreach (var characterKey in profile.ExpectedCharacterKeys ?? [])
                launchProfilesHash.Add(characterKey.Value, StringComparer.Ordinal);
        }

        return new DadSchedulerUiRevision(schedulerHash.ToHashCode(), launchProfilesHash.ToHashCode());
    }

    public DadSchedulerQueueSnapshot GetQueueSnapshot()
    {
        NormalizeQueue();
        NormalizeHistory();
        var visibleState = BuildVisibleState();
        var activeJobClone = levelingOperation?.OuterJob.Clone()
                             ?? (currentState.IsActive ? activeJob?.Clone() : null);
        return new DadSchedulerQueueSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            ActiveJob = activeJobClone,
            ActiveState = visibleState,
            ActiveQueueOwner = activeJobClone?.RequestedBy ?? currentState.RequestedBy,
            PendingJobs = configuration.SchedulerQueue
                .OrderBy(static job => job.NextEligibleTimeUtc ?? DateTime.MinValue)
                .ThenByDescending(static job => job.Priority)
                .ThenBy(static job => job.CreatedAtUtc)
                .Select(static job => job.Clone())
                .ToList(),
            RecentResults = configuration.SchedulerHistory
                .OrderByDescending(static result => result.CompletedAtUtc)
                .ThenByDescending(static result => result.StartedAtUtc)
                .Take(MaxSchedulerHistory)
                .Select(static result => result.Clone())
                .ToList(),
            Summary = IsSchedulerActive || IsCrewFormationActive
                ? $"Active {visibleState.JobType}: {visibleState.Summary}"
                : HasPendingCleanup
                    ? $"Cancellation cleanup pending for {pendingTakeoverCancellations.Count} wake takeover(s) and {pendingEarlyAssignmentCancellations.Count} early assignment route(s)."
                : configuration.SchedulerQueue.Count == 0
                    ? "Scheduler queue idle."
                    : $"{configuration.SchedulerQueue.Count} queued scheduler job(s).",
        };
    }

    public DadScheduleSnapshot GetScheduleSnapshot()
    {
        NormalizeSchedules();
        NormalizeScheduleHistory();
        var activeRun = configuration.ActiveScheduleRun ?? new DadScheduleRunState();
        return new DadScheduleSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            ActiveRun = activeRun.Clone(),
            Schedules = configuration.Schedules.Select(static schedule => schedule.Clone()).ToList(),
            RecentResults = configuration.ScheduleHistory
                .OrderByDescending(static result => result.CompletedAtUtc)
                .ThenByDescending(static result => result.StartedAtUtc)
                .Take(MaxScheduleHistory)
                .Select(static result => result.Clone())
                .ToList(),
            Summary = activeRun.IsActive
                ? activeRun.Summary
                : configuration.Schedules.Count == 0
                    ? "No schedules configured."
                    : $"{configuration.Schedules.Count} schedule(s) configured.",
        };
    }

    public DadScheduleDefinition CreateSchedule(string displayName)
    {
        NormalizeSchedules();
        var now = DateTime.UtcNow;
        var schedule = new DadScheduleDefinition
        {
            ScheduleId = Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Dad Schedule" : displayName.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }.Normalize();
        configuration.Schedules.Add(schedule);
        configuration.Save();
        return schedule.Clone();
    }

    public DadScheduleDefinition? DuplicateSchedule(string scheduleId, string displayName)
    {
        NormalizeSchedules();
        var source = FindSchedule(scheduleId);
        if (source == null)
            return null;

        var now = DateTime.UtcNow;
        var duplicate = source.Clone();
        duplicate.ScheduleId = Guid.NewGuid().ToString("N");
        duplicate.Revision = 1;
        duplicate.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? $"{source.DisplayName} Copy"
            : displayName.Trim();
        duplicate.CreatedAtUtc = now;
        duplicate.UpdatedAtUtc = now;
        duplicate.LastDailyResetUtc = null;
        duplicate.LastRunStartedAtUtc = null;
        duplicate.LastRunCompletedAtUtc = null;
        duplicate.LastRunStatus = DadScheduleRunStatus.Idle;
        duplicate.LastSummary = string.Empty;
        foreach (var entry in duplicate.Entries)
        {
            entry.EntryId = Guid.NewGuid().ToString("N");
            entry.CreatedAtUtc = now;
            entry.UpdatedAtUtc = now;
        }

        duplicate.Normalize();
        configuration.Schedules.Add(duplicate);
        configuration.Save();
        return duplicate.Clone();
    }

    public bool DeleteSchedule(string scheduleId)
    {
        NormalizeSchedules();
        var schedule = FindSchedule(scheduleId);
        if (schedule == null)
            return false;

        var active = configuration.ActiveScheduleRun ?? new DadScheduleRunState();
        if (active.IsActive && string.Equals(active.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase))
            return false;

        configuration.Schedules.Remove(schedule);
        configuration.Save();
        return true;
    }

    public DadScheduleDefinition? UpdateSchedule(DadScheduleDefinition incoming)
    {
        NormalizeSchedules();
        var normalized = incoming.Clone().Normalize();
        var existing = FindSchedule(normalized.ScheduleId);
        if (existing == null)
            return null;

        normalized.Revision = existing.Revision + 1;
        normalized.CreatedAtUtc = existing.CreatedAtUtc;
        normalized.UpdatedAtUtc = DateTime.UtcNow;
        normalized.LastDailyResetUtc = existing.LastDailyResetUtc;
        normalized.LastRunStartedAtUtc = existing.LastRunStartedAtUtc;
        normalized.LastRunCompletedAtUtc = existing.LastRunCompletedAtUtc;
        normalized.LastRunStatus = existing.LastRunStatus;
        normalized.LastSummary = existing.LastSummary;
        var index = configuration.Schedules.IndexOf(existing);
        configuration.Schedules[index] = normalized;
        configuration.Save();
        return normalized.Clone();
    }

    public DadScheduleAttachmentResult AttachSavedPlanToSchedule(
        string scheduleId,
        DadPlannerGroup group,
        bool dadMutationLocked)
    {
        NormalizeSchedules();
        if (dadMutationLocked || IsSchedulerActive || configuration.ActiveScheduleRun?.IsActive == true)
        {
            return new DadScheduleAttachmentResult
            {
                Disposition = DadScheduleAttachmentDisposition.MutationLocked,
                ScheduleId = scheduleId?.Trim() ?? string.Empty,
                GroupId = group?.GroupId?.Trim() ?? string.Empty,
                Summary = "The Plan was saved, but schedule attachment is disabled while DAD or scheduler state is mutating.",
            };
        }

        var schedule = FindSchedule(scheduleId);
        if (schedule == null)
        {
            return new DadScheduleAttachmentResult
            {
                Disposition = DadScheduleAttachmentDisposition.ScheduleMissing,
                ScheduleId = scheduleId?.Trim() ?? string.Empty,
                GroupId = group?.GroupId?.Trim() ?? string.Empty,
                Summary = "The Plan was saved, but the selected Schedule no longer exists.",
            };
        }

        var result = DadScheduleRules.AttachSavedPlan(schedule, group, DateTime.UtcNow);
        if (result.Added)
            configuration.Save();
        return result;
    }

    public DadScheduleRunState StartScheduleRun(string scheduleId, bool dryRun, string requestedBy)
    {
        NormalizeSchedules();
        NormalizeScheduleHistory();
        NormalizeQueue();
        NormalizeHistory();
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        if (IsCrewFormationActive)
        {
            return DadScheduleRules.BlockRun(new DadScheduleRunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                ScheduleId = scheduleId?.Trim() ?? string.Empty,
                ScheduleName = "Schedule",
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "schedule" : requestedBy.Trim(),
            }, "Crew Formation is active; schedules cannot start until it releases scheduler ownership.", DateTime.UtcNow);
        }
        if (!dryRun && !configuration.RunAsServerDad)
        {
            return DadScheduleRules.BlockRun(new DadScheduleRunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                ScheduleId = scheduleId?.Trim() ?? string.Empty,
                ScheduleName = "Schedule",
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "schedule" : requestedBy.Trim(),
            }, "Only Dad Coordinator may run schedules.", DateTime.UtcNow);
        }

        if (configuration.ActiveScheduleRun.IsActive)
        {
            if (string.Equals(configuration.ActiveScheduleRun.ScheduleId, scheduleId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return configuration.ActiveScheduleRun.Clone();

            return DadScheduleRules.BlockRun(new DadScheduleRunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                ScheduleId = scheduleId?.Trim() ?? string.Empty,
                ScheduleName = "Schedule",
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "schedule" : requestedBy.Trim(),
            }, $"Schedule '{configuration.ActiveScheduleRun.ScheduleName}' is already active as run {configuration.ActiveScheduleRun.RunId}.", DateTime.UtcNow);
        }

        var schedule = FindSchedule(scheduleId);
        if (schedule == null)
        {
            return DadScheduleRules.BlockRun(new DadScheduleRunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                ScheduleId = scheduleId?.Trim() ?? string.Empty,
                ScheduleName = "Missing schedule",
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "schedule" : requestedBy.Trim(),
            }, $"Schedule '{scheduleId}' could not be resolved.", DateTime.UtcNow);
        }

        var state = BeginScheduleRun(schedule, dryRun, manualRun: true, requestedBy, DateTime.UtcNow);
        configuration.Save();
        return state.Clone();
    }

    public bool CancelScheduleRun(string reason)
        => CancelScheduleRun(string.Empty, reason);

    public bool CancelScheduleRun(string runId, string reason)
    {
        NormalizeSchedules();
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        if (!configuration.ActiveScheduleRun.IsActive)
            return false;
        if (!string.IsNullOrWhiteSpace(runId) &&
            !string.Equals(configuration.ActiveScheduleRun.RunId, runId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var jobId = configuration.ActiveScheduleRun.ActiveSchedulerJobId;
        if (!string.IsNullOrWhiteSpace(jobId))
            CancelScheduledJob(jobId, string.IsNullOrWhiteSpace(reason) ? "Schedule cancelled." : reason);

        configuration.ActiveScheduleRun = DadScheduleRules.CancelRun(
            configuration.ActiveScheduleRun,
            string.IsNullOrWhiteSpace(reason) ? "Schedule cancelled." : reason,
            DateTime.UtcNow);
        FinalizeScheduleRun(configuration.ActiveScheduleRun);
        configuration.Save();
        return true;
    }

    public DadScheduleRetryResult EvaluateFailedEntryRetry(
        DadScheduleRetryRequest request,
        bool dadWorkActive)
        => EvaluateFailedEntryRetryCore(request, dadWorkActive, startRetry: false);

    public DadScheduleRetryResult RetryFailedEntry(
        DadScheduleRetryRequest request,
        bool dadWorkActive)
        => EvaluateFailedEntryRetryCore(request, dadWorkActive, startRetry: true);

    private DadScheduleRetryResult EvaluateFailedEntryRetryCore(
        DadScheduleRetryRequest request,
        bool dadWorkActive,
        bool startRetry)
    {
        request ??= new DadScheduleRetryRequest();
        NormalizeSchedules();
        NormalizeScheduleHistory();
        NormalizeQueue();
        NormalizeHistory();
        var failedRunId = request.FailedRunId?.Trim() ?? string.Empty;
        var result = new DadScheduleRetryResult
        {
            FailedRunId = failedRunId,
            ActiveRun = (configuration.ActiveScheduleRun ?? new DadScheduleRunState()).Clone(),
        };
        var failedMatches = configuration.ScheduleHistory.Where(candidate =>
            string.Equals(candidate.RunId, failedRunId, StringComparison.OrdinalIgnoreCase)).ToList();
        var failed = failedMatches.Count == 1 ? failedMatches[0] : null;
        if (failed == null)
        {
            result.Summary = "The failed schedule run is missing or no longer unique in persisted history.";
            return result;
        }

        result.FailureKind = failed.FailureKind;
        if (!DadScheduleRules.IsRetryableFailure(failed.FailureKind))
        {
            result.Summary = $"Schedule failure kind {failed.FailureKind} cannot be resumed.";
            return result;
        }
        if (!DadScheduleRules.TryValidateRetryAvailability(
                configuration.ActiveScheduleRun?.IsActive ?? false,
                IsSchedulerActive,
                dadWorkActive,
                configuration.SchedulerQueue.Count,
                HasPendingCleanup,
                out var activityBlocker))
        {
            result.Summary = activityBlocker;
            return result;
        }

        var schedule = FindSchedule(failed.ScheduleId);
        if (schedule == null)
        {
            result.Summary = "The failed run's Schedule disappeared.";
            return result;
        }
        if (!DadScheduleRules.TryCreateRetryState(
                failed,
                schedule,
                request.RequestedBy,
                DateTime.UtcNow,
                out var retryState,
                out var blocker))
        {
            result.Summary = blocker;
            return result;
        }

        result.Eligible = true;
        result.Summary = $"Failed entry {retryState.CurrentEntryIndex + 1}, repeat {retryState.RepeatIteration} can be resumed without changing prior history.";
        if (!startRetry)
            return result;

        configuration.ActiveScheduleRun = retryState;
        schedule.LastRunStartedAtUtc = retryState.StartedAtUtc;
        schedule.LastRunStatus = retryState.Status;
        schedule.LastSummary = retryState.Summary;
        result.Retried = true;
        result.ActiveRun = retryState.Clone();
        result.Summary = retryState.Summary;
        configuration.Save();
        return result;
    }

    public DadScheduledCrewJob EnqueueScheduledPreset(DadPlannerGroup group, DadScheduledPresetRequest request)
        => EnqueueScheduledPresetWithDisposition(group, request).Job;

    internal DadSchedulerEnqueueResult EnqueueScheduledPresetWithDisposition(
        DadPlannerGroup group,
        DadScheduledPresetRequest request)
    {
        NormalizeQueue();
        var duplicate = DadSchedulerSubmissionRules.FindDuplicate(
            group.GroupId,
            levelingOperation?.OuterJob ?? activeJob,
            IsSchedulerActive,
            configuration.SchedulerQueue,
            PendingCleanupJobs());
        if (duplicate != null)
            return duplicate;

        var job = new DadScheduledCrewJob
        {
            JobType = request.JobType,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            Enabled = request.Enabled,
            DryRun = request.DryRun,
            CreatedAtUtc = DateTime.UtcNow,
            NextEligibleTimeUtc = request.NextEligibleTimeUtc,
            Cadence = TimeSpan.FromHours(Math.Max(0, request.CadenceHours)),
            RequestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? "scheduler" : request.RequestedBy.Trim(),
            Priority = request.Priority,
            MapMode = request.MapMode,
            MapRunTemplate = request.MapRunTemplate?.Trim() ?? string.Empty,
            TargetCharacters = request.TargetCharacters?.Select(static target => target.Clone()).ToList() ?? [],
            TargetCharacterKeys = request.TargetCharacterKeys == null ? [] : [.. request.TargetCharacterKeys],
        };
        job.StatusSummary = BuildQueuedJobSummary(job);

        configuration.SchedulerQueue.Add(job);
        configuration.Save();
        return new DadSchedulerEnqueueResult
        {
            Disposition = DadSchedulerEnqueueDisposition.Added,
            Job = job.Clone(),
        };
    }

    public DadScheduledCrewJob EnqueueRosterUpdate(DadRosterRefreshPlan plan, DadAccountRosterCatalog catalog)
    {
        NormalizeQueue();
        plan.CharacterRefs ??= [];
        plan.AccountKeys ??= [];
        plan.CharacterKeys ??= [];
        var hasExplicitTargets = plan.CharacterRefs.Count > 0 || plan.AccountKeys.Count > 0 || plan.CharacterKeys.Count > 0;
        var targets = catalog.Characters
            .Where(character =>
                !hasExplicitTargets && character.NeedsRosterUpdate ||
                plan.CharacterRefs.Any(reference => DadRosterIdentity.Matches(character, reference)) ||
                plan.CharacterKeys.Any(key => string.Equals(key.Value, character.CharacterKey.Value, StringComparison.OrdinalIgnoreCase)) ||
                plan.AccountKeys.Any(key => DadRosterIdentity.SameAccount(key, character.AccountKey)))
            .Where(character => !character.AccountKey.IsEmpty && !character.CharacterKey.IsEmpty)
            .Select(DadRosterIdentity.From)
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var job = new DadScheduledCrewJob
        {
            JobType = DadSchedulerJobType.RosterUpdate,
            PresetName = "Roster update",
            DryRun = plan.DryRun,
            CreatedAtUtc = DateTime.UtcNow,
            RequestedBy = "roster-update",
            TargetCharacters = targets.Select(static target => target.Clone()).ToList(),
            TargetAccountKeys = targets.Select(static target => target.AccountKey).Where(static key => !key.IsEmpty).DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase).ToList(),
            TargetCharacterKeys = targets.Select(static target => target.CharacterKey).Where(static key => !key.IsEmpty).DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase).ToList(),
            StatusSummary = targets.Count == 0
                ? "Roster update queued with no matching target characters."
                : $"Queued roster update for {targets.Count} character(s).",
        };

        configuration.SchedulerQueue.Add(job);
        configuration.Save();
        return job.Clone();
    }

    public bool CancelScheduledJob(string jobId, string reason)
    {
        NormalizeQueue();
        var job = configuration.SchedulerQueue.FirstOrDefault(candidate =>
            string.Equals(candidate.JobId, jobId, StringComparison.OrdinalIgnoreCase));
        if (job != null)
        {
            configuration.SchedulerQueue.Remove(job);
            RecordTerminalResult(job, DadSchedulerPresetPhase.Cancelled, string.IsNullOrWhiteSpace(reason)
                ? $"Scheduler job {jobId} cancelled before start."
                : reason);
            configuration.Save();
            return true;
        }

        if (levelingOperation != null &&
            (string.Equals(levelingOperation.OuterJob.JobId, jobId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(currentState.JobId, jobId, StringComparison.OrdinalIgnoreCase)))
        {
            CancelLevelingOperation(string.IsNullOrWhiteSpace(reason) ? $"Leveling Mode job {jobId} cancelled." : reason);
            return true;
        }

        if (activeJob != null &&
            string.Equals(activeJob.JobId, jobId, StringComparison.OrdinalIgnoreCase) &&
            currentState.IsActive)
        {
            Cancel(string.IsNullOrWhiteSpace(reason) ? $"Scheduler job {jobId} cancelled." : reason);
            return true;
        }

        if (PendingCleanupJobs().Any(job =>
                string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase)))
        {
            UpdatePendingTakeoverCancellations();
            UpdatePendingEarlyAssignmentCancellations();
            return true;
        }

        return false;
    }

    public int ClearAccountData()
    {
        NormalizeQueue();
        if (levelingOperation != null)
            CancelLevelingOperation("Scheduler account data cleared.");
        CancelNonTerminalTakeovers("Scheduler account data cleared.");
        var clearedJobs = configuration.SchedulerQueue.Count;
        configuration.SchedulerQueue.Clear();
        activeJob = null;
        levelingOperation = null;
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
        frozenEarlyAssignments = [];
        frozenPlannerRequest = null;
        postWakeLevelTargetsEvaluated = false;
        currentState = new DadSchedulerPresetState
        {
            Phase = DadSchedulerPresetPhase.Idle,
            Summary = "Scheduler account data cleared.",
        };
        dependencyGateCommitted = false;
        configuration.ActiveScheduleRun = new DadScheduleRunState
        {
            Summary = "Scheduler account data cleared.",
        };
        nextRefreshUtc = DateTime.MinValue;
        return clearedJobs;
    }

    public IReadOnlyList<DadLaunchProfile> GetLaunchProfiles()
    {
        NormalizeLaunchProfiles();
        return configuration.LaunchProfiles.Select(static profile => profile.Clone()).ToList();
    }

    public int ImportLaunchProfilesFromBootDirectory()
    {
        if (!configuration.RunAsServerDad)
            return 0;

        NormalizeLaunchProfiles();
        if (!Directory.Exists(ClientBootDirectory))
            return 0;

        var imported = 0;
        foreach (var path in Directory.GetFiles(ClientBootDirectory, "*.bat"))
        {
            if (configuration.LaunchProfiles.Any(profile =>
                    string.Equals(profile.BatchPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            configuration.LaunchProfiles.Add(new DadLaunchProfile
            {
                ProfileId = Guid.NewGuid().ToString("N"),
                DisplayName = Path.GetFileNameWithoutExtension(path),
                BatchPath = path,
                Enabled = false,
                AllowAutoStart = false,
                DryRun = true,
            });
            imported++;
        }

        if (imported > 0)
            configuration.Save();

        return imported;
    }

    public DadLaunchProfileUpdateAck UpdateLaunchProfile(DadLaunchProfileUpdateRequest request)
    {
        if (!configuration.RunAsServerDad)
        {
            return new DadLaunchProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = "Only Dad Coordinator may update launch profiles.",
            };
        }

        NormalizeLaunchProfiles();
        var incoming = request.Profile?.Clone() ?? new DadLaunchProfile();
        incoming.Normalize();
        var existing = configuration.LaunchProfiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, incoming.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return new DadLaunchProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"Launch profile {incoming.ProfileId} does not exist.",
            };
        }

        if (request.ExpectedRevision != existing.Revision)
        {
            return new DadLaunchProfileUpdateAck
            {
                RequestId = request.RequestId,
                RevisionConflict = true,
                Summary = $"Launch profile '{existing.DisplayName}' changed; refresh before saving.",
                Profile = existing.Clone(),
            };
        }

        var blocker = ValidateLaunchProfile(incoming, existing.ProfileId);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            return new DadLaunchProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = blocker,
                Profile = existing.Clone(),
            };
        }

        incoming.Revision = existing.Revision + 1;
        var index = configuration.LaunchProfiles.IndexOf(existing);
        configuration.LaunchProfiles[index] = incoming;
        configuration.Save();
        return new DadLaunchProfileUpdateAck
        {
            RequestId = request.RequestId,
            Accepted = true,
            Summary = $"Saved launch profile '{incoming.DisplayName}'.",
            Profile = incoming.Clone(),
        };
    }

    private string ValidateLaunchProfile(DadLaunchProfile profile, string existingProfileId)
    {
        if (string.IsNullOrWhiteSpace(profile.BatchPath))
            return "Launch profile batch path is required.";
        if (!IsAllowedBootBatchPath(profile.BatchPath, ClientBootDirectory))
            return $"Launch profile must reference a .bat under {ClientBootDirectory}.";
        if (!File.Exists(profile.BatchPath))
            return $"Launch profile batch path not found: {profile.BatchPath}.";
        var normalizedIncomingPath = Path.GetFullPath(profile.BatchPath);
        if (configuration.LaunchProfiles.Any(existing =>
                !string.Equals(existing.ProfileId, existingProfileId, StringComparison.OrdinalIgnoreCase) &&
                IsAllowedBootBatchPath(existing.BatchPath, ClientBootDirectory) &&
                string.Equals(Path.GetFullPath(existing.BatchPath), normalizedIncomingPath, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Another launch profile already uses {profile.BatchPath}.";
        }

        foreach (var characterKey in profile.ExpectedCharacterKeys)
        {
            var character = rosterCatalogService.FindCharacter(new DadRosterCharacterRef
            {
                AccountKey = profile.AccountKey,
                CharacterKey = characterKey,
            });
            if (character == null)
                return $"Expected character {characterKey} does not belong to account {profile.AccountKey}.";
        }

        return string.Empty;
    }

    public DadSchedulerPreview BuildPreview(
        DadPlannerGroup? group,
        DadPlannerRunRequestPreview plannerRequestPreview)
    {
        var pool = characterIntelligenceService.CurrentPool;
        var launchProfiles = BuildNormalizedLaunchProfileSnapshot();
        var preview = new DadSchedulerPreview
        {
            GeneratedAtUtc = DateTime.UtcNow,
            GroupId = group?.GroupId ?? string.Empty,
            PresetName = group?.DisplayName ?? "Auto roster",
            Phase = currentState.IsActive ? currentState.Phase : DadSchedulerPresetPhase.Resolving,
            PlannerRequestPreview = plannerRequestPreview,
            LaunchProfiles = launchProfiles.Select(static profile => profile.Clone()).ToList(),
        };

        if (group == null)
        {
            BlockPreview(preview, "Select a saved preset before using the scheduler.");
            return preview;
        }

        if (group.Slots.Count == 0)
        {
            BlockPreview(preview, $"Preset '{group.DisplayName}' has no scheduler slots.");
            return preview;
        }

        var effectiveGroup = BuildEffectiveSchedulerGroup(group, plannerRequestPreview);
        var priorRuntimeSlots = currentState.IsActive &&
                                string.Equals(currentState.GroupId, group.GroupId, StringComparison.OrdinalIgnoreCase)
            ? currentState.Slots
            : [];
        preview.Slots = BuildSlotStates(effectiveGroup, pool, priorRuntimeSlots, launchProfiles: launchProfiles);
        var blockers = preview.Slots
            .Select(static slot => slot.BlockedReason)
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!plannerRequestPreview.CanSchedule || plannerRequestPreview.Request == null || blockers.Count > 0)
        {
            BlockPreview(
                preview,
                FirstNonEmpty(
                    DadPlannerValidationRules.BuildSchedulerTerminalReason(plannerRequestPreview, blockers),
                    "Scheduler preview is not startable."));
            return preview;
        }

        preview.ReadyToStart = preview.Slots.All(static slot => slot.Ready);
        preview.CanStart = true;
        if (!AllSlotDependenciesReady(preview.Slots))
        {
            preview.Phase = DadSchedulerPresetPhase.WaitingForDependencies;
            preview.StatusSummary = $"Scheduler will wait for fresh required-plugin truth from all {preview.Slots.Count} selected slot(s).";
        }
        else
        {
            preview.StatusSummary = preview.ReadyToStart
            ? $"Scheduler ready: all {preview.Slots.Count} preset slot(s) are online."
            : $"Scheduler can start: {preview.Slots.Count(static slot => slot.Ready)}/{preview.Slots.Count} preset slot(s) ready.";
        }
        return preview;
    }

    public DadSchedulerPresetState StartPreset(
        DadPlannerGroup group,
        DadPlannerRunRequestPreview plannerRequestPreview,
        bool dryRun,
        DadScheduledCrewJob? job = null)
    {
        if (group.LevelingMode?.Enabled == true)
            return StartLevelingOperation(group, dryRun, job);

        return StartOrdinaryPreset(group, plannerRequestPreview, dryRun, job);
    }

    private DadSchedulerPresetState StartOrdinaryPreset(
        DadPlannerGroup group,
        DadPlannerRunRequestPreview plannerRequestPreview,
        bool dryRun,
        DadScheduledCrewJob? job = null)
    {
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
        var preview = BuildPreview(group, plannerRequestPreview);
        var levelSeek = EvaluateLevelSeek(group, plannerRequestPreview);
        var effectiveGroup = BuildEffectiveSchedulerGroup(group, plannerRequestPreview);
        activeJob = job?.Clone() ?? new DadScheduledCrewJob
        {
            JobType = DadSchedulerJobType.ScheduledPreset,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            DryRun = dryRun,
            RequestedBy = "scheduler",
        };
        // Freeze exact targets before early job assignment, wake, launch, or relog. Their evidence is
        // intentionally re-read after every exact worker and requested job is ready.
        frozenPlannerRequest = levelSeek.ShouldSkip ? null : CloneRunRequest(plannerRequestPreview.Request);
        frozenLevelSeekGroup = frozenPlannerRequest != null && levelSeek.HasTargetedRows
            ? DadSchedulerGroupCloneRules.CloneWithSlots(effectiveGroup, effectiveGroup.Slots)
            : null;
        postWakeLevelTargetsEvaluated = false;
        frozenEarlyAssignments = frozenPlannerRequest == null
            ? []
            : BuildFrozenEarlyAssignments(frozenPlannerRequest, preview.Slots, activeJob);
        DadImmutableCommandRegistration? requestRegistration = null;
        if (frozenPlannerRequest != null)
        {
            var payload = DadIpcJson.Serialize(frozenPlannerRequest);
            requestRegistration = frozenRequestRegistry.Register(
                frozenPlannerRequest.RequestId,
                payload,
                payload,
                $"scheduler:{activeJob.RequestedBy}:{group.GroupId}");
        }
        currentState = new DadSchedulerPresetState
        {
            SchedulerRunId = Guid.NewGuid().ToString("N"),
            JobId = activeJob.JobId,
            JobType = activeJob.JobType,
            RequestedBy = activeJob.RequestedBy,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            DryRun = dryRun,
            PlannerRequestId = plannerRequestPreview.Request?.RequestId ?? string.Empty,
            Slots = preview.Slots.Select(static slot => slot.Clone()).ToList(),
            ScheduleId = activeJob.ScheduleId,
            ScheduleRunId = activeJob.ScheduleRunId,
            ScheduleEntryId = activeJob.ScheduleEntryId,
            ScheduleEntryIndex = activeJob.ScheduleEntryIndex,
            ScheduleRepeatIteration = activeJob.ScheduleRepeatIteration,
            ScheduleCadence = activeJob.ScheduleCadence,
        };
        dependencyGateCommitted = false;
        takeoverDiagnosticStates.Clear();
        slotContradictionTrackers.Clear();
        lastLoggedSchedulerPhase = null;
        activeSchedulerModuleId = plannerRequestPreview.Request?.Orchestration?.ModuleTarget ?? DadModuleId.None;
        strictRevalidationTracker = new DadStrictPlannerRevalidationTracker();
        InitializeWakeTimestamps(currentState.Slots, currentState.StartedAtUtc);
        nextRefreshUtc = DateTime.MinValue;

        if (levelSeek.ShouldSkip)
        {
            currentState.Phase = DadSchedulerPresetPhase.Skipped;
            currentState.SkipKind = DadSchedulerSkipKind.LevelSeek;
            currentState.Summary = $"Skipped preset '{group.DisplayName}': {levelSeek.Summary}";
            currentState.CompletedAtUtc = DateTime.UtcNow;
            currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
            RecordTerminalResult(currentState);
            return CurrentState;
        }

        if (requestRegistration?.Disposition == DadImmutableCommandDisposition.Collision)
        {
            var collision = requestRegistration;
            log.Error(
                "[dad] Immutable scheduler request collision command={CommandId} originalProducerRoute={OriginalProducerRoute} incomingProducerRoute={IncomingProducerRoute} originalPayload={OriginalPayload} incomingPayload={IncomingPayload}.",
                collision.CommandId,
                collision.OriginalProducerRoute,
                collision.IncomingProducerRoute,
                collision.OriginalPayload,
                collision.IncomingPayload);
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.BlockedReason = $"Immutable command ID collision for scheduler request {collision.CommandId}.";
            currentState.Summary = currentState.BlockedReason;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return CurrentState;
        }

        if (frozenPlannerRequest != null)
        {
            log.Information(
                "[dad] Frozen scheduler request request={RequestId} module={ModuleId} jobs={FrozenJobs} queue={QueueTarget}.",
                frozenPlannerRequest.RequestId,
                frozenPlannerRequest.Orchestration.ModuleTarget,
                string.Join(",", frozenEarlyAssignments.Select(static assignment =>
                    $"{assignment.Request.AssignedSlotId}:{assignment.Request.RequiredJobId?.ToString() ?? "current"}")),
                DescribeQueueTarget(frozenPlannerRequest));
        }

        if (!preview.CanStart)
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = $"Scheduler blocked for preset '{group.DisplayName}': {preview.BlockedReason}";
            currentState.BlockedReason = preview.BlockedReason;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return CurrentState;
        }

        var frozenAssignmentBlocker = ValidateFrozenEarlyAssignments(
            frozenPlannerRequest,
            currentState.Slots,
            frozenEarlyAssignments);
        if (!string.IsNullOrWhiteSpace(frozenAssignmentBlocker))
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = frozenAssignmentBlocker;
            currentState.BlockedReason = frozenAssignmentBlocker;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
            RecordTerminalResult(currentState);
            return CurrentState;
        }

        if (dryRun)
        {
            currentState.Phase = DadSchedulerPresetPhase.Completed;
            currentState.Summary = $"Scheduler dry run ready for preset '{group.DisplayName}': {preview.StatusSummary}";
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return CurrentState;
        }

        var rouletteTarget = frozenPlannerRequest?.DailyMsq?.QueueTarget;
        var checkedRows = effectiveGroup.Slots
            .Where(static slot => slot.SkipIfDailyRouletteRewardReceived)
            .ToList();
        if (DadDailyRewardPreflightRules.IsEligible(
                effectiveGroup.ActivityMode,
                activeJob.ScheduleCadence,
                activeJob.ScheduleId,
                activeJob.ScheduleRunId,
                activeJob.ScheduleEntryId,
                rouletteTarget,
                checkedRows.Count))
        {
            var checkedSlotIds = checkedRows
                .Select(static slot => slot.SlotId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            frozenOrdinarySlots = currentState.Slots.Select(static slot => slot.Clone()).ToList();
            currentState.Slots = currentState.Slots
                .Where(slot => checkedSlotIds.Contains(slot.SlotId))
                .Select(static slot => slot.Clone())
                .ToList();
            dailyRewardPreflight = new DailyRewardPreflightSession
            {
                Target = rouletteTarget!.Clone(),
                CheckedSlotCount = currentState.Slots.Count,
                SlotDeadlineUtc = DateTime.UtcNow + ResolveDailyRewardPreflightTimeout(),
            };
            InitializeWakeTimestamps(currentState.Slots, currentState.StartedAtUtc);
            currentState.Phase = DadSchedulerPresetPhase.DailyRewardPreflight;
            currentState.Summary = $"DailyReset reward preflight will inspect {currentState.Slots.Count} checked effective row(s) before ordinary execution.";
            currentState.UpdatedAtUtc = DateTime.UtcNow;
            LogSchedulerPhaseTransition();
            return CurrentState;
        }

        var transientRuntimeReadiness = !plannerRequestPreview.CanStart && plannerRequestPreview.CanSchedule;
        currentState.Phase = DadSchedulerRuntimeWakeRules.ResolveInitialPhase(
            plannerRequestPreview.CanStart,
            plannerRequestPreview.CanSchedule,
            preview.ReadyToStart);
        currentState.Summary = transientRuntimeReadiness
            ? plannerRequestPreview.StatusSummary
            : preview.StatusSummary;
        LogSchedulerPhaseTransition();
        return CurrentState;
    }

    private DadSchedulerPresetState StartLevelingOperation(
        DadPlannerGroup group,
        bool dryRun,
        DadScheduledCrewJob? job)
    {
        var outerJob = job?.Clone() ?? new DadScheduledCrewJob
        {
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            DryRun = dryRun,
            RequestedBy = "scheduler",
        };
        outerJob.JobType = DadSchedulerJobType.LevelingOperation;
        outerJob.GroupId = group.GroupId;
        outerJob.PresetName = group.DisplayName;
        outerJob.DryRun = dryRun;

        var outerState = new DadSchedulerPresetState
        {
            SchedulerRunId = Guid.NewGuid().ToString("N"),
            JobId = outerJob.JobId,
            JobType = DadSchedulerJobType.LevelingOperation,
            RequestedBy = outerJob.RequestedBy,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            Phase = DadSchedulerPresetPhase.LevelingBetweenChildren,
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            DryRun = dryRun,
            Summary = $"Leveling Mode '{group.DisplayName}' is compiling child 1.",
            ScheduleId = outerJob.ScheduleId,
            ScheduleRunId = outerJob.ScheduleRunId,
            ScheduleEntryId = outerJob.ScheduleEntryId,
            ScheduleEntryIndex = outerJob.ScheduleEntryIndex,
            ScheduleRepeatIteration = outerJob.ScheduleRepeatIteration,
            ScheduleCadence = outerJob.ScheduleCadence,
        };
        activeJob = outerJob.Clone();
        levelingOperation = new LevelingOperationSession
        {
            SourceGroup = DadSchedulerGroupCloneRules.CloneWithSlots(group, group.Slots ?? []),
            OuterJob = outerJob,
            OuterState = outerState,
        };
        levelingOperation.IssuedIds.Add(outerJob.JobId);
        return StartNextLevelingChild();
    }

    private DadSchedulerPresetState StartNextLevelingChild()
    {
        var operation = levelingOperation;
        if (operation == null)
            return CurrentState;
        if (levelingChildBuilder == null)
            return FinishLevelingOperation(DadSchedulerPresetPhase.Blocked, "Leveling Mode compiler is unavailable.");

        DadLevelingChildBuild build;
        try
        {
            build = levelingChildBuilder(operation.SourceGroup, operation.Iteration) ?? new DadLevelingChildBuild();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Leveling Mode child compiler failed for preset {PresetName} iteration {Iteration}.", operation.SourceGroup.DisplayName, operation.Iteration);
            return FinishLevelingOperation(DadSchedulerPresetPhase.Blocked, $"Leveling Mode child {operation.Iteration} could not compile: {ex.Message}");
        }

        operation.ChildBuild = build;
        var compilation = build.Compilation ?? new DadLevelingCompilation();
        if (compilation.Status == DadLevelingCompilationStatus.Complete)
        {
            return FinishLevelingOperation(
                DadSchedulerPresetPhase.Completed,
                $"Leveling Mode '{operation.SourceGroup.DisplayName}' completed after {Math.Max(0, operation.Iteration - 1)} child run(s): {compilation.Summary}");
        }
        if (compilation.Status != DadLevelingCompilationStatus.Ready || !compilation.CanStartChild)
        {
            return FinishLevelingOperation(
                DadSchedulerPresetPhase.Blocked,
                $"Leveling Mode '{operation.SourceGroup.DisplayName}' blocked before child {operation.Iteration}: {compilation.Summary}");
        }
        if (build.PlannerPreview?.Request == null)
            return FinishLevelingOperation(DadSchedulerPresetPhase.Blocked, $"Leveling Mode child {operation.Iteration} did not produce an ordinary planner request.");
        if (!string.Equals(build.PlannerPreview.Request.RequestId, compilation.ChildRequestId, StringComparison.Ordinal))
            return FinishLevelingOperation(DadSchedulerPresetPhase.Blocked, $"Leveling Mode child {operation.Iteration} request identity did not match its frozen compilation.");
        if (!operation.IssuedIds.Add(compilation.ChildJobId) ||
            !operation.IssuedIds.Add(compilation.ChildRequestId))
        {
            return FinishLevelingOperation(DadSchedulerPresetPhase.Blocked, $"Leveling Mode child {operation.Iteration} reused a prior job or request identity.");
        }

        var childJob = operation.OuterJob.Clone();
        childJob.JobId = compilation.ChildJobId;
        childJob.JobType = DadSchedulerJobType.LevelingChild;
        childJob.ParentOperationJobId = operation.OuterJob.JobId;
        childJob.LevelingIteration = operation.Iteration;
        childJob.CreatedAtUtc = DateTime.UtcNow;
        childJob.ScheduleId = string.Empty;
        childJob.ScheduleRunId = string.Empty;
        childJob.ScheduleEntryId = string.Empty;
        childJob.ScheduleEntryIndex = -1;
        childJob.ScheduleRepeatIteration = 0;
        childJob.ScheduleCadence = DadScheduleCadence.Manual;

        operation.RefreshingRoster = false;
        operation.RefreshRows = [];
        operation.RefreshDeadlineUtc = default;
        operation.OuterState.Summary = $"Leveling Mode child {operation.Iteration}: {compilation.Summary}";
        operation.OuterState.UpdatedAtUtc = DateTime.UtcNow;
        StartOrdinaryPreset(compilation.ChildGroup!, build.PlannerPreview, operation.OuterJob.DryRun, childJob);
        currentState.ParentOperationJobId = operation.OuterJob.JobId;
        currentState.LevelingIteration = operation.Iteration;
        return CurrentState;
    }

    private DadSchedulerPresetState FinishLevelingOperation(DadSchedulerPresetPhase phase, string summary)
    {
        var operation = levelingOperation;
        if (operation == null)
            return CurrentState;

        var outer = operation.OuterState;
        outer.Phase = phase;
        outer.Summary = summary;
        outer.BlockedReason = phase is DadSchedulerPresetPhase.Blocked or DadSchedulerPresetPhase.TimedOut or DadSchedulerPresetPhase.Cancelled
            ? summary
            : string.Empty;
        outer.CompletedAtUtc = DateTime.UtcNow;
        outer.UpdatedAtUtc = outer.CompletedAtUtc.Value;
        outer.PlannerStarted = operation.Iteration > 1 || currentState.PlannerStarted;
        outer.PlannerRequestId = string.Empty;
        outer.Slots = currentState.Slots.Select(static slot => slot.Clone()).ToList();

        activeJob = operation.OuterJob.Clone();
        levelingOperation = null;
        currentState = outer;
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
        frozenEarlyAssignments = [];
        frozenPlannerRequest = null;
        postWakeLevelTargetsEvaluated = false;
        dependencyGateCommitted = false;
        activeSchedulerModuleId = DadModuleId.None;
        RecordTerminalResult(currentState);
        return CurrentState;
    }

    private DadSchedulerPresetState BuildVisibleState()
    {
        var operation = levelingOperation;
        if (operation == null)
            return currentState.Clone();

        var visible = operation.OuterState.Clone();
        visible.Phase = DadSchedulerPresetPhase.LevelingBetweenChildren;
        visible.CompletedAtUtc = null;
        visible.PlannerStarted = currentState.PlannerStarted;
        visible.PlannerRequestId = currentState.PlannerRequestId;
        visible.Slots = currentState.Slots.Select(static slot => slot.Clone()).ToList();
        visible.UpdatedAtUtc = currentState.UpdatedAtUtc;
        visible.Summary = operation.RefreshingRoster
            ? $"Leveling Mode child {operation.Iteration} completed; refreshing exact job ledgers ({operation.RefreshRows.Count(static row => row.Completed)}/{operation.RefreshRows.Count})."
            : currentState.Phase == DadSchedulerPresetPhase.StartedPlanner
                ? $"Leveling Mode child {operation.Iteration} is running request {currentState.PlannerRequestId}; jobs and duty remain frozen until it ends."
                : $"Leveling Mode child {operation.Iteration}: {currentState.Summary}";
        return visible;
    }

    public DadLevelSeekEvaluation EvaluateLevelSeek(
        DadPlannerGroup group,
        DadPlannerRunRequestPreview plannerRequestPreview)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(plannerRequestPreview);

        var effectiveGroup = BuildEffectiveSchedulerGroup(group, plannerRequestPreview);
        return DadLevelSeekEvaluationRules.Evaluate(
            effectiveGroup,
            plannerRequestPreview.Request?.StopPolicy,
            characterIntelligenceService.CurrentPool);
    }

    private bool TrySkipSatisfiedPostWakeLevelTargets()
    {
        if (postWakeLevelTargetsEvaluated)
            return false;

        postWakeLevelTargetsEvaluated = true;
        if (frozenLevelSeekGroup == null || frozenPlannerRequest == null)
            return false;

        characterIntelligenceService.RefreshLocalCharacterPool(
            "scheduler-stop-policy",
            logRefresh: false);
        var evaluation = DadLevelSeekEvaluationRules.Evaluate(
            frozenLevelSeekGroup,
            frozenPlannerRequest.StopPolicy,
            characterIntelligenceService.RequestPeerSnapshots());
        if (!evaluation.ShouldSkip)
        {
            currentState.Summary =
                $"Post-readiness LevelSeek will continue to planner dispatch. {evaluation.DescribeEvidence()}";
            currentState.UpdatedAtUtc = DateTime.UtcNow;
            return false;
        }

        var summary =
            $"Skipped preset '{currentState.PresetName}' after exact worker readiness: {evaluation.DescribeEvidence()}";
        CancelNonTerminalTakeovers(summary);
        currentState.Phase = DadSchedulerPresetPhase.Skipped;
        currentState.SkipKind = DadSchedulerSkipKind.LevelSeek;
        currentState.Summary = summary;
        currentState.BlockedReason = string.Empty;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
        frozenEarlyAssignments = [];
        frozenPlannerRequest = null;
        frozenLevelSeekGroup = null;
        postWakeLevelTargetsEvaluated = false;
        RecordTerminalResult(currentState);
        return true;
    }

    public void WakeForRuntimeReadiness(DadWorkerSessionId workerSessionId)
    {
        nextRefreshUtc = DateTime.MinValue;
        DadSchedulerRuntimeWakeRules.MakeMatchingTakeoverChecksDue(currentState.Slots, workerSessionId);
    }

    public void Update(
        Func<string, DadPlannerGroup?> groupResolver,
        Func<string, DadPlannerRunRequestPreview?> plannerPreviewBuilder,
        Func<DadRunRequest, DadRunResult> startPlannerRequest,
        Func<DadRunResult>? visibleRunProvider = null)
        => UpdateCore(
            groupResolver,
            plannerPreviewBuilder,
            (request, _) => startPlannerRequest(request),
            visibleRunProvider);

    internal void UpdateWithScheduleRepeatBoundary(
        Func<string, DadPlannerGroup?> groupResolver,
        Func<string, DadPlannerRunRequestPreview?> plannerPreviewBuilder,
        Func<DadRunRequest, DadScheduleRepeatBoundary, DadRunResult> startPlannerRequest,
        Func<DadRunResult>? visibleRunProvider = null)
        => UpdateCore(groupResolver, plannerPreviewBuilder, startPlannerRequest, visibleRunProvider);

    private void UpdateCore(
        Func<string, DadPlannerGroup?> groupResolver,
        Func<string, DadPlannerRunRequestPreview?> plannerPreviewBuilder,
        Func<DadRunRequest, DadScheduleRepeatBoundary, DadRunResult> startPlannerRequest,
        Func<DadRunResult>? visibleRunProvider)
    {
        UpdatePendingTakeoverCancellations();
        UpdatePendingEarlyAssignmentCancellations();
        LogSchedulerPhaseTransition();
        TickScheduleEnqueue();

        var visibleRun = visibleRunProvider?.Invoke() ?? DadRunResult.Idle();
        UpdateCompletedPlannerAssignmentCleanup(visibleRun);
        if (IsCrewFormationActive &&
            crewFormation!.Status.Phase != DadCrewFormationPhase.Preparing)
        {
            UpdateCrewFormationAfterPreparation(visibleRun);
            return;
        }
        if (IsCrewFormationActive && !currentState.IsActive)
        {
            FinishCrewFormation(
                currentState.Phase == DadSchedulerPresetPhase.Cancelled
                    ? DadCrewFormationPhase.Cancelled
                    : DadCrewFormationPhase.Blocked,
                FirstNonEmpty(currentState.BlockedReason, currentState.Summary));
            return;
        }

        if (levelingOperation != null)
        {
            var allowOrdinaryChildAdvance = UpdateLevelingOperation(visibleRun);
            UpdateActiveScheduleRun(visibleRun);
            if (levelingOperation == null || !allowOrdinaryChildAdvance)
                return;
        }
        else
        {
            UpdateActiveScheduleRun(visibleRun);
        }

        if (!currentState.IsActive)
        {
            if (IsCrewFormationActive)
                return;
            if (!TryStartNextQueuedJob(groupResolver, plannerPreviewBuilder) || !currentState.IsActive)
                return;
        }

        if (currentState.DryRun)
            return;

        if (DateTime.UtcNow < nextRefreshUtc && currentState.Phase != DadSchedulerPresetPhase.ReadyToStart)
            return;

        nextRefreshUtc = DateTime.UtcNow + RefreshInterval;

        if (currentState.JobType == DadSchedulerJobType.RosterUpdate)
        {
            UpdateRosterUpdateJob();
            return;
        }

        if (currentState.JobType == DadSchedulerJobType.MapCrew)
        {
            UpdateMapCrewJob(groupResolver);
            return;
        }

        if (currentState.Phase == DadSchedulerPresetPhase.DailyRewardPreflight)
        {
            UpdateDailyRewardPreflight();
            return;
        }

        if (frozenPlannerRequest == null)
        {
            BlockActive("Scheduler has no frozen planner request.");
            return;
        }

        var groupPreviewSlots = currentState.Slots;
        var previewSlots = RebuildActiveSlots(groupPreviewSlots);
        currentState.Slots = previewSlots;
        currentState.UpdatedAtUtc = DateTime.UtcNow;

        if (HasActiveRebindCleanup)
        {
            currentState.Phase = DadSchedulerPresetPhase.Resolving;
            currentState.Summary = "A stable-account client reconnected on a new worker session; waiting for exact old-route cancellation acknowledgement before mutation.";
            return;
        }

        var preMutationSlotBlockers = currentState.Slots
            .Select(static slot => slot.BlockedReason)
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (preMutationSlotBlockers.Count > 0)
        {
            BlockActive(string.Join(" | ", preMutationSlotBlockers));
            return;
        }

        if (!DadDependencyMutationBoundaryRules.CanCross(
                dependencyGateCommitted,
                currentState.Slots.Select(static slot => slot.DependenciesReady)))
        {
            currentState.Phase = DadSchedulerPresetPhase.WaitingForDependencies;
            currentState.Summary = BuildReadinessWaitSummary(currentState, dependenciesCommitted: false);
            return;
        }
        dependencyGateCommitted = true;

        var assignmentsAcknowledged = DeliverEarlyJobAssignments(out var assignmentBlocker);
        if (!string.IsNullOrWhiteSpace(assignmentBlocker))
        {
            BlockActive(assignmentBlocker);
            return;
        }

        if (!AdvanceWakeTakeoverPipelines(currentState.Slots, out var barrierBlocker))
        {
            BlockActive(barrierBlocker);
            return;
        }

        DadPlannerValidationRules.StampReadyTransitions(
            currentState.Slots,
            groupPreviewSlots,
            DateTime.UtcNow);

        foreach (var slot in currentState.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
            {
                BlockActive(slot.BlockedReason);
                return;
            }

            if (IsParticipantReadyTimedOut(slot, out var timeoutReason))
            {
                currentState.Phase = DadSchedulerPresetPhase.TimedOut;
                currentState.BlockedReason = timeoutReason;
                currentState.Summary = currentState.BlockedReason;
                currentState.CompletedAtUtc = DateTime.UtcNow;
                currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
                RecordTerminalResult(currentState);
                return;
            }

            if (slot.Ready)
                continue;
        }

        if (!currentState.Slots.All(static slot => slot.Ready))
        {
            currentState.Phase = ResolveWaitingPhase(currentState.Slots, dependencyGateCommitted);
            currentState.Summary = BuildReadinessWaitSummary(currentState, dependencyGateCommitted);
            return;
        }

        if (!assignmentsAcknowledged)
        {
            currentState.Phase = DadSchedulerPresetPhase.Resolving;
            currentState.Summary = "All characters are ready; waiting for every exact requested-job assignment acknowledgement.";
            currentState.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        if (TrySkipSatisfiedPostWakeLevelTargets())
            return;

        var autoPartyAuthorization = autoPartyAuthorizationGate?.Invoke(frozenPlannerRequest)
            ?? new DadAutoPartyAuthorizationDecision(
                DadAutoPartyAuthorizationState.NotRequired,
                "dad-autoparty-not-configured",
                Guid.Empty);
        if (autoPartyAuthorization.State == DadAutoPartyAuthorizationState.Waiting)
        {
            currentState.Phase = DadSchedulerPresetPhase.WaitingForAutoPartyAuthorization;
            currentState.Summary = $"All local readiness gates passed; waiting inside DAD for AutoParty authorization ({autoPartyAuthorization.SafeCode}).";
            currentState.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }
        if (autoPartyAuthorization.State == DadAutoPartyAuthorizationState.Denied)
        {
            BlockActive($"AutoParty authorization denied ({autoPartyAuthorization.SafeCode}).");
            return;
        }

        currentState.Phase = DadSchedulerPresetPhase.StartingPlanner;
        currentState.Summary = $"Scheduler ready; starting preset '{currentState.PresetName}'.";
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        LogSchedulerPhaseTransition();
        if (!strictRevalidationTracker.TryClaimStart())
            return;

        if (TryStartPreparedCrewFormation())
            return;

        var strictRequest = CloneRunRequest(frozenPlannerRequest)!;
        var repeatBoundary = ResolveScheduleRepeatBoundary();
        var result = startPlannerRequest(strictRequest, repeatBoundary);
        currentState.PlannerStarted = result.Status != DadRunStatus.Rejected;
        currentState.Phase = currentState.PlannerStarted
            ? DadSchedulerPresetPhase.StartedPlanner
            : DadSchedulerPresetPhase.Blocked;
        var startRejection = FirstNonEmpty(result.BlockedReason, result.FailureReason, result.Summary);
        currentState.Summary = currentState.PlannerStarted
            ? $"Scheduler started preset '{currentState.PresetName}': {result.Summary}"
            : $"Scheduler could not start preset '{currentState.PresetName}': {startRejection}";
        currentState.BlockedReason = currentState.PlannerStarted ? string.Empty : startRejection;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
        RecordTerminalResult(currentState);
    }

    private bool TryStartPreparedCrewFormation()
    {
        var session = crewFormation;
        if (session?.Status.IsActive != true ||
            session.Status.Phase != DadCrewFormationPhase.Preparing ||
            !string.Equals(
                session.Status.SchedulerRunId,
                currentState.SchedulerRunId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (frozenPlannerRequest == null)
        {
            FinishCrewFormation(
                DadCrewFormationPhase.Blocked,
                "Crew Formation lost its frozen planner request before group creation.");
            return true;
        }

        if (session.Status.Mode == DadCrewFormationMode.RegularParty)
        {
            var request = CloneRunRequest(frozenPlannerRequest)!;
            request.Orchestration.AutoPartyFormationOnly = true;
            session.Status.Phase = DadCrewFormationPhase.StartingRegularParty;
            session.Status.Summary = $"Starting regular party formation for '{session.Status.SourcePresetName}'.";
            session.Status.UpdatedAtUtc = DateTime.UtcNow;

            var result = startCrewRegularParty!(request);
            currentState.PlannerStarted = result.Status != DadRunStatus.Rejected;
            currentState.Phase = currentState.PlannerStarted
                ? DadSchedulerPresetPhase.StartedPlanner
                : DadSchedulerPresetPhase.Blocked;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
            if (!currentState.PlannerStarted)
            {
                var rejection = FirstNonEmpty(result.BlockedReason, result.FailureReason, result.Summary);
                currentState.BlockedReason = rejection;
                currentState.Summary = rejection;
                FinishCrewFormation(DadCrewFormationPhase.Blocked, rejection);
            }
            else
            {
                currentState.Summary = $"Crew Formation started exact coordinator request {request.RequestId}.";
                session.Status.Summary = currentState.Summary;
            }

            return true;
        }

        if (session.Status.Mode == DadCrewFormationMode.AlliancePartyFinder)
        {
            session.Status.Phase = DadCrewFormationPhase.CreatingAllianceListing;
            session.Status.Summary = $"Creating the private alliance recruitment for '{session.Status.SourcePresetName}'.";
            session.Status.UpdatedAtUtc = DateTime.UtcNow;
            var created = createCrewAllianceParty!(
                session.Status.RunId,
                session.EffectiveGroup,
                session.AlliancePreview);
            var rejection = FirstNonEmptyOrEmpty(
                created.CreateRejected ? created.CreatePreflightBlocker : string.Empty,
                created.State == DadAllianceRecruitmentState.Blocked ? created.Summary : string.Empty,
                string.IsNullOrWhiteSpace(created.RecruitmentId)
                    ? "Alliance Party Finder did not return an owned recruitment ID."
                    : string.Empty);
            if (!string.IsNullOrWhiteSpace(rejection))
            {
                currentState.Phase = DadSchedulerPresetPhase.Blocked;
                currentState.BlockedReason = rejection;
                currentState.Summary = rejection;
                currentState.CompletedAtUtc = DateTime.UtcNow;
                currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
                FinishCrewFormation(DadCrewFormationPhase.Blocked, rejection);
                return true;
            }

            session.Status.RecruitmentId = created.RecruitmentId;
            session.Status.Summary = created.Summary;
            currentState.PlannerStarted = true;
            currentState.Phase = DadSchedulerPresetPhase.StartedPlanner;
            currentState.Summary = $"Crew Formation owns alliance recruitment {created.RecruitmentId}.";
            currentState.CompletedAtUtc = DateTime.UtcNow;
            currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
            return true;
        }

        FinishCrewFormation(
            DadCrewFormationPhase.Blocked,
            "Crew Formation has no supported formation mode.");
        return true;
    }

    private void UpdateCrewFormationAfterPreparation(DadRunResult visibleRun)
    {
        var session = crewFormation;
        if (session?.Status.IsActive != true)
            return;

        if (session.Status.Mode == DadCrewFormationMode.RegularParty)
        {
            if (!string.Equals(visibleRun.RequestId, session.Status.RequestId, StringComparison.Ordinal))
            {
                session.Status.Summary = $"Waiting for exact coordinator request {session.Status.RequestId}.";
                session.Status.UpdatedAtUtc = DateTime.UtcNow;
                return;
            }

            if (DadCrewToolsRules.IsExactRegularGroupReady(
                    visibleRun,
                    session.Status.RequestId))
            {
                session.Status.Phase = DadCrewFormationPhase.RegularGroupReady;
                session.Status.Summary = "Regular party is formed and held at GroupReady. It will not queue.";
                session.Status.UpdatedAtUtc = DateTime.UtcNow;
                return;
            }

            if (visibleRun.Status is DadRunStatus.Queued
                or DadRunStatus.WaitingForParticipants
                or DadRunStatus.Running)
            {
                session.Status.Summary = visibleRun.Summary;
                session.Status.UpdatedAtUtc = DateTime.UtcNow;
                return;
            }

            if (session.Status.Phase == DadCrewFormationPhase.Disbanding &&
                visibleRun.Status == DadRunStatus.Cancelled)
            {
                FinishCrewFormation(
                    DadCrewFormationPhase.Completed,
                    "Crew Tools disband completed and released the held formation run.");
                return;
            }

            var terminalSummary = FirstNonEmpty(
                visibleRun.BlockedReason,
                visibleRun.FailureReason,
                visibleRun.Summary,
                $"Coordinator request {session.Status.RequestId} ended with {visibleRun.Status}.");
            FinishCrewFormation(
                visibleRun.Status == DadRunStatus.Cancelled
                    ? DadCrewFormationPhase.Cancelled
                    : DadCrewFormationPhase.Blocked,
                terminalSummary);
            return;
        }

        if (session.Status.Mode != DadCrewFormationMode.AlliancePartyFinder)
        {
            FinishCrewFormation(
                DadCrewFormationPhase.Blocked,
                "Crew Formation entered an unsupported runtime mode.");
            return;
        }

        var alliance = getCrewAllianceStatus!();
        if (!string.Equals(
                alliance.RecruitmentId,
                session.Status.RecruitmentId,
                StringComparison.Ordinal))
        {
            FinishCrewFormation(
                DadCrewFormationPhase.Blocked,
                "The owned alliance recruitment identity changed; refusing to control an unrelated PF operation.");
            return;
        }

        if (session.Status.Phase == DadCrewFormationPhase.AllianceCleanup)
        {
            session.Status.Summary = alliance.Summary;
            session.Status.UpdatedAtUtc = DateTime.UtcNow;
            if (!alliance.OwnsRecruitment &&
                alliance.State is DadAllianceRecruitmentState.Stopped
                    or DadAllianceRecruitmentState.Complete)
            {
                FinishCrewFormation(
                    DadCrewFormationPhase.Blocked,
                    FirstNonEmpty(session.TerminalFailure, "Alliance formation failed."));
            }
            return;
        }

        if (DadCrewToolsRules.ShouldGrabExactAlliance(
                alliance,
                session.Status.RecruitmentId,
                session.Status.GrabRequested))
        {
            session.Status.GrabRequested = true;
            session.Status.Phase = DadCrewFormationPhase.GrabbingAlliance;
            var grabbing = grabCrewAllianceParty!();
            session.Status.Summary = grabbing.Summary;
            session.Status.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        if (DadCrewToolsRules.IsExactAllianceComplete(
                alliance,
                session.Status.RecruitmentId))
        {
            FinishCrewFormation(
                DadCrewFormationPhase.Completed,
                "Alliance group formation completed with exact subgroup verification and PF cleanup.");
            return;
        }

        if (alliance.State == DadAllianceRecruitmentState.Blocked)
        {
            session.TerminalFailure = FirstNonEmpty(alliance.Summary, "Alliance formation was blocked.");
            if (DadAlliancePartyFinderRules.CanStop(alliance))
            {
                session.Status.Phase = DadCrewFormationPhase.AllianceCleanup;
                stopCrewAllianceParty!("Crew Formation failed; cleaning up only its owned alliance recruitment.");
                var stopping = getCrewAllianceStatus!();
                session.Status.Summary = stopping.Summary;
                session.Status.UpdatedAtUtc = DateTime.UtcNow;
                return;
            }

            FinishCrewFormation(DadCrewFormationPhase.Blocked, session.TerminalFailure);
            return;
        }

        if (alliance.State == DadAllianceRecruitmentState.Stopped)
        {
            FinishCrewFormation(
                DadCrewFormationPhase.Blocked,
                FirstNonEmpty(alliance.Summary, "Alliance formation stopped before completion."));
            return;
        }

        session.Status.Summary = alliance.Summary;
        session.Status.UpdatedAtUtc = DateTime.UtcNow;
    }

    internal string MarkCrewFormationDisbanding(string requestId)
    {
        var session = crewFormation;
        if (session?.Status.IsActive != true ||
            session.Status.Mode != DadCrewFormationMode.RegularParty ||
            session.Status.Phase != DadCrewFormationPhase.RegularGroupReady ||
            !string.Equals(session.Status.RequestId, requestId, StringComparison.Ordinal))
        {
            return "The exact regular Crew Formation run is not held at GroupReady.";
        }

        session.Status.Phase = DadCrewFormationPhase.Disbanding;
        session.Status.Summary = "Guarded disband is in progress.";
        session.Status.UpdatedAtUtc = DateTime.UtcNow;
        return string.Empty;
    }

    internal void CancelCrewFormation(string reason)
    {
        if (!IsCrewFormationActive)
            return;

        reason = string.IsNullOrWhiteSpace(reason) ? "Crew Formation cancelled." : reason.Trim();
        if (crewFormation!.Status.Mode == DadCrewFormationMode.AlliancePartyFinder &&
            DadAlliancePartyFinderRules.CanStop(getCrewAllianceStatus?.Invoke()))
        {
            stopCrewAllianceParty?.Invoke(reason);
        }

        FinishCrewFormation(DadCrewFormationPhase.Cancelled, reason);
    }

    private void FinishCrewFormation(DadCrewFormationPhase phase, string summary)
    {
        var session = crewFormation;
        if (session == null)
            return;

        summary = string.IsNullOrWhiteSpace(summary) ? phase.ToString() : summary.Trim();
        CancelNonTerminalTakeovers(summary);
        session.Status.Phase = phase;
        session.Status.Summary = summary;
        session.Status.BlockedReason = phase == DadCrewFormationPhase.Blocked ? summary : string.Empty;
        session.Status.UpdatedAtUtc = DateTime.UtcNow;
        session.Status.CompletedAtUtc = session.Status.UpdatedAtUtc;

        currentState.Phase = phase switch
        {
            DadCrewFormationPhase.Completed => DadSchedulerPresetPhase.Completed,
            DadCrewFormationPhase.Cancelled => DadSchedulerPresetPhase.Cancelled,
            _ => DadSchedulerPresetPhase.Blocked,
        };
        currentState.Summary = summary;
        currentState.BlockedReason = session.Status.BlockedReason;
        currentState.CompletedAtUtc = session.Status.CompletedAtUtc;
        currentState.UpdatedAtUtc = session.Status.UpdatedAtUtc;
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
        frozenEarlyAssignments = [];
        frozenPlannerRequest = null;
        postWakeLevelTargetsEvaluated = false;
        dependencyGateCommitted = false;
        activeSchedulerModuleId = DadModuleId.None;
        activeJob = null;
    }

    private static T CloneCrewValue<T>(T value)
        where T : class
        => DadIpcJson.Deserialize<T>(DadIpcJson.Serialize(value))
           ?? throw new InvalidOperationException($"Could not freeze {typeof(T).Name} for Crew Formation.");

    private bool UpdateLevelingOperation(DadRunResult visibleRun)
    {
        var operation = levelingOperation;
        if (operation == null)
            return false;

        if (operation.RefreshingRoster)
        {
            UpdateLevelingRosterRefresh();
            return false;
        }

        DadRunResult? childRun = null;
        if (currentState.Phase == DadSchedulerPresetPhase.StartedPlanner)
            childRun = ResolveScheduleDadRunResult(currentState.PlannerRequestId, visibleRun);
        var disposition = DadLevelingOperationRules.ClassifyChild(
            currentState.Phase,
            childRun?.Status,
            operation.OuterJob.DryRun);

        switch (disposition)
        {
            case DadLevelingChildDisposition.Waiting:
                operation.OuterState.UpdatedAtUtc = DateTime.UtcNow;
                operation.OuterState.Summary = currentState.Phase == DadSchedulerPresetPhase.StartedPlanner
                    ? $"Leveling Mode child {operation.Iteration} is waiting for exact run {currentState.PlannerRequestId} to finish."
                    : $"Leveling Mode child {operation.Iteration}: {currentState.Summary}";
                return currentState.IsActive;

            case DadLevelingChildDisposition.CompleteDryRun:
                FinishLevelingOperation(
                    DadSchedulerPresetPhase.Completed,
                    $"Leveling Mode dry run compiled one immutable child successfully: {operation.ChildBuild.Compilation.Summary}");
                return false;

            case DadLevelingChildDisposition.Cancel:
                FinishLevelingOperation(
                    DadSchedulerPresetPhase.Cancelled,
                    $"Leveling Mode stopped because child {operation.Iteration} was cancelled: {FirstNonEmpty(childRun?.Summary ?? string.Empty, currentState.Summary)}");
                return false;

            case DadLevelingChildDisposition.Fail:
                if (childRun == null)
                    CancelNonTerminalTakeovers($"Leveling Mode child {operation.Iteration} ended before planner completion.");
                var failedPhase = currentState.Phase == DadSchedulerPresetPhase.TimedOut || childRun?.Status == DadRunStatus.TimedOut
                    ? DadSchedulerPresetPhase.TimedOut
                    : DadSchedulerPresetPhase.Blocked;
                FinishLevelingOperation(
                    failedPhase,
                    $"Leveling Mode stopped without replay after child {operation.Iteration} failed: {FirstNonEmpty(childRun?.FailureReason ?? string.Empty, childRun?.BlockedReason ?? string.Empty, childRun?.Summary ?? string.Empty, currentState.BlockedReason, currentState.Summary)}");
                return false;

            case DadLevelingChildDisposition.RefreshAndContinue:
                if (HasPendingCleanup)
                {
                    operation.OuterState.Summary = $"Leveling Mode child {operation.Iteration} completed; waiting for ordinary child cleanup before roster refresh.";
                    operation.OuterState.UpdatedAtUtc = DateTime.UtcNow;
                    return false;
                }
                if (operation.RefreshDeadlineUtc == default)
                    operation.RefreshDeadlineUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                if (DateTime.UtcNow >= operation.RefreshDeadlineUtc)
                {
                    FinishLevelingOperation(
                        DadSchedulerPresetPhase.TimedOut,
                        $"Leveling Mode stopped after child {operation.Iteration}: exact roster refresh route did not become ready; the child will not replay.");
                    return false;
                }
                BeginLevelingRosterRefresh();
                UpdateLevelingRosterRefresh();
                return false;

            default:
                return false;
        }
    }

    private void BeginLevelingRosterRefresh()
    {
        var operation = levelingOperation;
        if (operation == null || operation.RefreshingRoster)
            return;

        var participants = BuildParticipantSet(characterIntelligenceService.CurrentPool);
        var rows = new List<LevelingRosterRefreshRow>();
        foreach (var selection in operation.ChildBuild.Compilation.Slots)
        {
            var exact = participants
                .Where(participant => DadRosterIdentity.SameAccount(participant.ManagedAccountKey, selection.AccountKey))
                .Where(participant => string.Equals(participant.ActiveCharacterKey.Value, selection.CharacterKey.Value, StringComparison.OrdinalIgnoreCase))
                .Where(participant => string.Equals(participant.Character.CharacterKey, selection.CharacterKey.Value, StringComparison.OrdinalIgnoreCase))
                .Where(participant => selection.ContentId == 0 || participant.Character.ContentId == selection.ContentId)
                .ToList();
            if (exact.Count != 1)
            {
                FinishLevelingOperation(
                    DadSchedulerPresetPhase.Blocked,
                    $"Leveling Mode stopped after child {operation.Iteration}: exact world-ready refresh route for {selection.SlotId} {selection.CharacterKey.Value} was {(exact.Count == 0 ? "unavailable" : "ambiguous")}.");
                return;
            }
            if (!exact[0].IsLocalClient && !transportService.IsWorkerOnline(exact[0].WorkerSessionId))
            {
                FinishLevelingOperation(
                    DadSchedulerPresetPhase.Blocked,
                    $"Leveling Mode stopped after child {operation.Iteration}: exact refresh route for {selection.SlotId} disconnected; the child will not replay.");
                return;
            }
            if (!exact[0].WorldReadyStable)
            {
                operation.OuterState.Summary = $"Leveling Mode child {operation.Iteration} completed; waiting for {selection.SlotId} to regain stable world readiness before exact roster refresh.";
                operation.OuterState.UpdatedAtUtc = DateTime.UtcNow;
                return;
            }

            rows.Add(new LevelingRosterRefreshRow
            {
                Selection = selection,
                Participant = exact[0].Clone(),
                Command = new DadRosterRefreshCommandDto
                {
                    CommandId = Guid.NewGuid().ToString("N"),
                    RequestedAtUtc = DateTime.UtcNow,
                    AccountKey = selection.AccountKey,
                    CharacterKey = selection.CharacterKey,
                    ContentId = selection.ContentId,
                    SaveAfterRefresh = true,
                    DryRun = false,
                },
            });
        }

        operation.RefreshRows = rows;
        operation.RefreshingRoster = true;
        operation.RefreshDeadlineUtc = operation.RefreshDeadlineUtc == default
            ? DateTime.UtcNow + TimeSpan.FromMinutes(2)
            : operation.RefreshDeadlineUtc;
        operation.OuterState.Summary = $"Leveling Mode child {operation.Iteration} completed; refreshing exact job ledgers for {rows.Count} slot(s).";
        operation.OuterState.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void UpdateLevelingRosterRefresh()
    {
        var operation = levelingOperation;
        if (operation == null || !operation.RefreshingRoster)
            return;
        if (DateTime.UtcNow >= operation.RefreshDeadlineUtc)
        {
            FinishLevelingOperation(
                DadSchedulerPresetPhase.TimedOut,
                $"Leveling Mode stopped after child {operation.Iteration}: exact roster refresh timed out; the child will not replay.");
            return;
        }

        foreach (var row in operation.RefreshRows.Where(static row => !row.Completed))
        {
            if (!row.Participant.IsLocalClient && !transportService.IsWorkerOnline(row.Participant.WorkerSessionId))
            {
                FinishLevelingOperation(
                    DadSchedulerPresetPhase.Blocked,
                    $"Leveling Mode stopped after child {operation.Iteration}: refresh route for {row.Selection.SlotId} disconnected; the child will not replay.");
                return;
            }

            DadRosterRefreshResultDto? result;
            try
            {
                result = row.Participant.IsLocalClient
                    ? rosterCatalogService.RefreshLocalRosterCharacter(row.Command, presenceService.BuildSnapshotCopy())
                    : transportService.SendRosterRefreshCommand(row.Participant, row.Command);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[dad] Leveling Mode exact roster refresh failed for {SlotId} {CharacterKey}.", row.Selection.SlotId, row.Selection.CharacterKey.Value);
                FinishLevelingOperation(
                    DadSchedulerPresetPhase.Blocked,
                    $"Leveling Mode stopped after child {operation.Iteration}: refresh for {row.Selection.SlotId} failed: {ex.Message}");
                return;
            }

            if (result == null)
                continue;
            if (!DadLevelingOperationRules.TryValidateExactRosterRefresh(row.Command, result, out var blocker))
            {
                FinishLevelingOperation(
                    DadSchedulerPresetPhase.Blocked,
                    $"Leveling Mode stopped after child {operation.Iteration}: {blocker} The child will not replay.");
                return;
            }

            if (!row.Participant.IsLocalClient)
                rosterCatalogService.RecordRefreshResult(result);
            rosterCatalogService.ApplyExactRefreshTruth(result, row.Participant);
            row.Completed = true;
        }

        if (!operation.RefreshRows.All(static row => row.Completed))
            return;

        characterIntelligenceService.RequestPeerSnapshots();
        operation.RefreshingRoster = false;
        operation.RefreshRows = [];
        operation.Iteration++;
        StartNextLevelingChild();
    }

    public void UpdatePendingTakeoverCancellations()
    {
        if (pendingTakeoverCancellations.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var participants = BuildParticipantSet(characterIntelligenceService.CurrentPool);
        foreach (var pair in pendingTakeoverCancellations.ToList())
        {
            var pending = pair.Value;
            if (now < pending.NextAttemptUtc)
                continue;

            pending.NextAttemptUtc = now + RefreshInterval;
            var slot = pending.Slot;
            var participant = slot.MatchedWorkerSessionId.IsEmpty
                ? DadSchedulerRoutingRules.ResolveExactConnectedClient(
                    slot.RequiredAccountKey,
                    participants,
                    transportService.IsWorkerOnline)
                : DadSchedulerRoutingRules.ResolveFrozenCancellationClient(
                    slot.MatchedWorkerSessionId,
                    participants,
                    transportService.IsWorkerOnline);
            if (participant == null ||
                (!participant.IsLocalClient && !transportService.IsWorkerOnline(participant.WorkerSessionId)))
            {
                continue;
            }

            var request = new DadWakeTakeoverRequestDto
            {
                SchedulerRunId = pending.SchedulerRunId,
                OperationToken = pending.SchedulerRunId,
                SlotId = slot.SlotId,
                AccountKey = slot.RequiredAccountKey,
                CharacterKey = slot.RequiredCharacterKey,
                RequestedAtUtc = pending.RequestedAtUtc,
                MessageKind = DadWakeTakeoverMessageKind.Cancel,
                CommitKind = DadWakeCommitKind.None,
            };
            var result = participant.IsLocalClient
                ? wakeTakeoverService.Handle(request)
                : transportService.SendWakeTakeoverRequest(participant, request);
            if (result == null || !DadSchedulerRoutingRules.IsTakeoverCancellationComplete(result))
                continue;

            pendingTakeoverCancellations.Remove(pair.Key);
            log.Information(
                "[dad] Pending wake cancellation completed: request={SchedulerRunId}, slot={SlotId}, account={AccountKey}, character={CharacterKey}, acknowledgement={AcknowledgementState}, reason={Reason}, result={Summary}.",
                pending.SchedulerRunId,
                slot.SlotId,
                slot.RequiredAccountKey,
                slot.RequiredCharacterKey,
                result.AcknowledgementState,
                pending.Reason,
                FirstNonEmpty(result.BlockedReason, result.Summary));
        }
    }

    public void UpdatePendingEarlyAssignmentCancellations()
    {
        if (pendingEarlyAssignmentCancellations.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var participants = BuildParticipantSet(characterIntelligenceService.CurrentPool);
        foreach (var pair in pendingEarlyAssignmentCancellations.ToList())
        {
            var pending = pair.Value;
            if (now < pending.NextAttemptUtc)
                continue;

            pending.NextAttemptUtc = now + RefreshInterval;
            var participant = participants.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.WorkerSessionId.Value,
                    pending.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase) &&
                (candidate.IsLocalClient || transportService.IsWorkerOnline(candidate.WorkerSessionId)));
            if (participant == null)
                continue;

            var command = new DadCancelCommandDto
            {
                RunId = pending.RunId,
                AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                CancellationState = DadRunCancellationState.Requested,
                Reason = pending.Reason,
            };
            var ack = participant.IsLocalClient
                ? presenceService.HandleCancelRun(command)
                : transportService.SendCancelRun(participant, command);
            if (ack is not { Acknowledged: true })
                continue;

            pendingEarlyAssignmentCancellations.Remove(pair.Key);
            log.Information(
                "[dad] Early requested-job cancellation acknowledged request={RequestId} slot={SlotId} worker={WorkerSessionId} reason={Reason} summary={Summary}.",
                pending.RunId,
                pending.SlotId,
                pending.WorkerSessionId,
                pending.Reason,
                ack.Summary);
        }
    }

    private bool DeliverEarlyJobAssignments(out string blocker)
    {
        blocker = string.Empty;
        if (frozenEarlyAssignments.Count == 0)
            return true;

        var participants = BuildParticipantSet(characterIntelligenceService.CurrentPool);
        var allAcknowledged = true;
        foreach (var assignment in frozenEarlyAssignments)
        {
            var slot = currentState.Slots.FirstOrDefault(candidate =>
                string.Equals(candidate.SlotId, assignment.Request.AssignedSlotId, StringComparison.OrdinalIgnoreCase));
            var route = slot is { MatchedWorkerSessionId.IsEmpty: false }
                ? DadSchedulerRoutingRules.ResolveFrozenConnectedClient(
                    assignment.Request.RequiredAccountKey,
                    slot.MatchedWorkerSessionId,
                    participants,
                    transportService.IsWorkerOnline)
                : null;
            if (slot == null ||
                slot.MatchedWorkerSessionId.IsEmpty ||
                !transportService.IsWorkerOnline(slot.MatchedWorkerSessionId))
            {
                route ??= DadSchedulerRoutingRules.ResolveExactConnectedClient(
                    assignment.Request.RequiredAccountKey,
                    participants,
                    transportService.IsWorkerOnline);
            }
            if (route == null)
            {
                allAcknowledged = false;
                LogEarlyAssignmentTransition(assignment, "waiting-route", "No sole exact stable-account route is currently available.");
                continue;
            }

            var workerId = route.WorkerSessionId.Value;
            if (assignment.AcknowledgedWorkerSessionIds.Contains(workerId))
                continue;

            assignment.AttemptedWorkerSessionIds.Add(workerId);
            var response = route.IsLocalClient
                ? presenceService.HandleWakeRequest(assignment.Request)
                : transportService.SendWakeRequest(route, assignment.Request);
            if (response == null)
            {
                allAcknowledged = false;
                LogEarlyAssignmentTransition(assignment, $"waiting-ack:{workerId}", "Typed assignment acknowledgement is missing; retrying idempotently.");
                continue;
            }

            if (!response.AcceptedAssignment)
            {
                blocker = $"Immutable requested-job assignment {assignment.CommandId} was rejected by worker {workerId}: {response.BlockerSummary}";
                LogEarlyAssignmentTransition(assignment, $"rejected:{workerId}", blocker);
                continue;
            }

            assignment.AcknowledgedWorkerSessionIds.Add(workerId);
            LogEarlyAssignmentTransition(
                assignment,
                $"acknowledged:{workerId}",
                $"Worker retained requested job {assignment.Request.RequiredJobId} before character readiness ({response.Snapshot.ActiveCharacterKey}).");
        }

        return string.IsNullOrWhiteSpace(blocker) && allAcknowledged;
    }

    private void LogEarlyAssignmentTransition(FrozenEarlyAssignment assignment, string state, string summary)
    {
        if (string.Equals(assignment.LastDiagnosticState, state, StringComparison.Ordinal))
            return;

        assignment.LastDiagnosticState = state;
        log.Information(
            "[dad] Early requested-job assignment transition command={CommandId} request={RequestId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} requestedJob={RequestedJobId} state={State} summary={Summary}.",
            assignment.CommandId,
            assignment.Request.RunId,
            assignment.Request.AssignedSlotId,
            assignment.Request.RequiredAccountKey,
            assignment.Request.RequiredCharacterKey,
            assignment.Request.RequiredContentId,
            assignment.Request.RequiredJobId?.ToString() ?? "(current)",
            state,
            summary);
    }

    public void TickScheduleEnqueue()
    {
        if (IsCrewFormationActive)
            return;

        NormalizeQueue();
        configuration.PlannerGroups ??= [];
        var now = DateTime.UtcNow;
        if (now < suppressAutomaticEnqueueUntilUtc)
            return;
        var changed = false;

        foreach (var group in configuration.PlannerGroups
                     .Where(static group => group.ScheduleEnabled)
                     .OrderByDescending(static group => group.SchedulePriority)
                     .ThenBy(static group => group.NextEligibleTimeUtc ?? DateTime.MinValue))
        {
            var nextEligible = group.NextEligibleTimeUtc ?? DateTime.MinValue;
            if (nextEligible > now)
                continue;

            var jobType = DadSchedulerJobType.ScheduledPreset;
            if (FindEquivalentActiveOrPendingJob(group.GroupId) == null)
            {
                var job = new DadScheduledCrewJob
                {
                    JobType = jobType,
                    GroupId = group.GroupId,
                    PresetName = group.DisplayName,
                    Enabled = true,
                    DryRun = false,
                    CreatedAtUtc = now,
                    NextEligibleTimeUtc = now,
                    Cadence = TimeSpan.FromHours(ResolveScheduleCadenceHours(group.ScheduleCadenceHours)),
                    RequestedBy = string.IsNullOrWhiteSpace(group.ScheduleRequester)
                        ? "schedule"
                        : group.ScheduleRequester.Trim(),
                    Priority = group.SchedulePriority,
                    MapMode = group.MapMode,
                    MapRunTemplate = group.MapRunTemplate?.Trim() ?? string.Empty,
                };
                job.StatusSummary = BuildQueuedJobSummary(job);
                configuration.SchedulerQueue.Add(job);
            }

            group.NextEligibleTimeUtc = now + TimeSpan.FromHours(ResolveScheduleCadenceHours(group.ScheduleCadenceHours));
            group.UpdatedAtUtc = now;
            changed = true;
        }

        changed |= TryStartDueDailySchedule(now);

        if (changed)
            configuration.Save();
    }

    private bool TryStartDueDailySchedule(DateTime now)
    {
        if (!configuration.RunAsServerDad)
            return false;

        NormalizeSchedules();
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        if (configuration.ActiveScheduleRun.IsActive)
            return false;

        var schedule = configuration.Schedules
            .Where(schedule => DadScheduleRules.IsDailyResetDue(schedule, now))
            .OrderBy(static schedule => schedule.LastDailyResetUtc ?? DateTime.MinValue)
            .ThenBy(static schedule => schedule.CreatedAtUtc)
            .FirstOrDefault();
        if (schedule == null)
            return false;

        BeginScheduleRun(schedule, dryRun: false, manualRun: false, requestedBy: "daily-schedule", now);
        return true;
    }

    public void Cancel(string reason)
    {
        if (IsCrewFormationActive)
        {
            CancelCrewFormation(reason);
            return;
        }

        if (levelingOperation != null)
        {
            CancelLevelingOperation(string.IsNullOrWhiteSpace(reason) ? "Leveling Mode cancelled." : reason);
            return;
        }
        if (!currentState.IsActive)
            return;

        CancelNonTerminalTakeovers(reason);
        currentState.Phase = DadSchedulerPresetPhase.Cancelled;
        currentState.Summary = string.IsNullOrWhiteSpace(reason) ? "Scheduler cancelled." : reason;
        currentState.BlockedReason = currentState.Summary;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        RecordTerminalResult(currentState);
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
    }

    public DadSchedulerStopAllResult StopAll(string reason, TimeSpan automaticEnqueueSuppression)
    {
        NormalizeQueue();
        NormalizeSchedules();
        reason = string.IsNullOrWhiteSpace(reason) ? "Stopped by DAD Stop-all." : reason;
        var result = new DadSchedulerStopAllResult();

        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        if (configuration.ActiveScheduleRun.IsActive)
        {
            configuration.ActiveScheduleRun = DadScheduleRules.CancelRun(
                configuration.ActiveScheduleRun,
                reason,
                DateTime.UtcNow);
            FinalizeScheduleRun(configuration.ActiveScheduleRun);
            result.ActiveScheduleCancelled = true;
        }

        if (IsCrewFormationActive)
        {
            CancelCrewFormation(reason);
            result.ActiveJobCancelled = true;
        }
        else if (levelingOperation != null)
        {
            CancelLevelingOperation(reason);
            result.ActiveJobCancelled = true;
        }
        else if (currentState.IsActive)
        {
            CancelNonTerminalTakeovers(reason);
            currentState.Phase = DadSchedulerPresetPhase.Cancelled;
            currentState.Summary = reason;
            currentState.BlockedReason = reason;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
            RecordTerminalResult(currentState);
            result.ActiveJobCancelled = true;
        }

        foreach (var job in configuration.SchedulerQueue.ToList())
        {
            RecordTerminalResult(job, DadSchedulerPresetPhase.Cancelled, reason);
            result.PendingJobsCancelled++;
        }
        configuration.SchedulerQueue.Clear();
        activeJob = null;
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
        frozenEarlyAssignments = [];
        frozenPlannerRequest = null;
        postWakeLevelTargetsEvaluated = false;
        takeoverDiagnosticStates.Clear();
        nextRefreshUtc = DateTime.MinValue;
        suppressAutomaticEnqueueUntilUtc = DateTime.UtcNow +
            (automaticEnqueueSuppression <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : automaticEnqueueSuppression);
        configuration.Save();
        result.Summary = $"Cancelled schedule={result.ActiveScheduleCancelled}, active job={result.ActiveJobCancelled}, pending jobs={result.PendingJobsCancelled}.";
        return result;
    }

    private void CancelLevelingOperation(string reason)
    {
        if (levelingOperation == null)
            return;

        reason = string.IsNullOrWhiteSpace(reason) ? "Leveling Mode cancelled." : reason.Trim();
        if (currentState.Phase == DadSchedulerPresetPhase.StartedPlanner)
        {
            try
            {
                cancelActiveLevelingChild?.Invoke();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[dad] Leveling Mode active child cancellation request failed.");
            }
        }
        else if (currentState.IsActive)
        {
            CancelNonTerminalTakeovers(reason);
        }

        FinishLevelingOperation(DadSchedulerPresetPhase.Cancelled, reason);
    }

    private bool TryStartNextQueuedJob(
        Func<string, DadPlannerGroup?> groupResolver,
        Func<string, DadPlannerRunRequestPreview?> plannerPreviewBuilder)
    {
        NormalizeQueue();
        if (!DadSchedulerSubmissionRules.CanStartNextQueuedJob(HasPendingCleanup))
            return false;

        var now = DateTime.UtcNow;
        var nextJob = configuration.SchedulerQueue
            .Where(static job => job.Enabled)
            .Where(job => !job.NextEligibleTimeUtc.HasValue || job.NextEligibleTimeUtc.Value <= now)
            .OrderByDescending(static job => job.Priority)
            .ThenBy(static job => job.CreatedAtUtc)
            .FirstOrDefault();
        if (nextJob == null)
            return false;

        configuration.SchedulerQueue.Remove(nextJob);
        configuration.Save();

        if (nextJob.JobType == DadSchedulerJobType.RosterUpdate)
        {
            StartRosterUpdateJob(nextJob);
            return true;
        }

        var group = groupResolver(nextJob.GroupId);
        if (group == null)
        {
            activeJob = nextJob.Clone();
            currentState = BuildBlockedQueuedState(nextJob, $"Queued preset '{nextJob.GroupId}' could not be resolved.");
            RecordTerminalResult(currentState);
            return true;
        }

        if (nextJob.JobType == DadSchedulerJobType.MapCrew)
        {
            StartMapCrewJob(nextJob, group);
            return true;
        }

        var preview = plannerPreviewBuilder(nextJob.GroupId);
        if (preview == null)
        {
            activeJob = nextJob.Clone();
            currentState = BuildBlockedQueuedState(nextJob, $"Queued preset '{nextJob.GroupId}' could not build planner preview.");
            RecordTerminalResult(currentState);
            return true;
        }

        StartPreset(group, preview, nextJob.DryRun, nextJob);
        return true;
    }

    private void StartMapCrewJob(DadScheduledCrewJob job, DadPlannerGroup group)
    {
        activeJob = job.Clone();
        lastLoggedSchedulerPhase = null;
        activeSchedulerModuleId = DadModuleId.Mixed;
        currentState = new DadSchedulerPresetState
        {
            SchedulerRunId = Guid.NewGuid().ToString("N"),
            JobId = job.JobId,
            JobType = DadSchedulerJobType.MapCrew,
            RequestedBy = job.RequestedBy,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            DryRun = job.DryRun,
            Summary = BuildMapCrewSummary(job, group, "starting"),
        };
        dependencyGateCommitted = false;

        var unsupported = BuildUnsupportedMapCrewBlocker(job.MapMode);
        if (!string.IsNullOrWhiteSpace(unsupported))
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = unsupported;
            currentState.BlockedReason = unsupported;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return;
        }

        if (group.Slots.Count == 0)
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = $"Map crew '{group.DisplayName}' has no saved preset slots.";
            currentState.BlockedReason = currentState.Summary;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return;
        }

        var pool = characterIntelligenceService.CurrentPool;
        currentState.Slots = BuildSlotStates(group, pool, []);
        InitializeWakeTimestamps(currentState.Slots, currentState.StartedAtUtc);
        var blockers = currentState.Slots
            .Select(static slot => slot.BlockedReason)
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockers.Count > 0)
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = string.Join(" | ", blockers);
            currentState.BlockedReason = currentState.Summary;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return;
        }

        if (job.DryRun)
        {
            currentState.Phase = DadSchedulerPresetPhase.Completed;
            currentState.Summary = BuildMapCrewSummary(job, group, $"dry run ready with {currentState.Slots.Count} slot(s)");
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return;
        }

        currentState.Phase = currentState.Slots.All(static slot => slot.Ready)
            ? DadSchedulerPresetPhase.ReadyToStart
            : ResolveWaitingPhase(currentState.Slots);
        currentState.Summary = BuildMapCrewSummary(job, group, $"{currentState.Slots.Count(static slot => slot.Ready)}/{currentState.Slots.Count} slot(s) ready");
        nextRefreshUtc = DateTime.MinValue;
    }

    private void StartRosterUpdateJob(DadScheduledCrewJob job)
    {
        activeJob = job.Clone();
        lastLoggedSchedulerPhase = null;
        activeSchedulerModuleId = DadModuleId.None;
        var pool = characterIntelligenceService.RequestPeerSnapshots();
        var catalog = rosterCatalogService.RefreshCatalog(pool, new DadRosterRefreshPlan
        {
            ForcePeerRefresh = true,
            IncludeHidden = true,
            IncludeIgnored = true,
        });
        var targets = ResolveRosterUpdateTargets(job, catalog);
        currentState = new DadSchedulerPresetState
        {
            SchedulerRunId = Guid.NewGuid().ToString("N"),
            JobId = job.JobId,
            JobType = DadSchedulerJobType.RosterUpdate,
            RequestedBy = job.RequestedBy,
            PresetName = "Roster update",
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            DryRun = job.DryRun,
        };
        dependencyGateCommitted = false;

        if (targets.Count == 0)
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = "Roster update has no target characters.";
            currentState.BlockedReason = currentState.Summary;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return;
        }

        var group = new DadPlannerGroup
        {
            GroupId = job.JobId,
            DisplayName = "Roster update",
            Slots = targets.Select((target, index) => new DadPlannerGroupSlot
            {
                SlotId = $"Update{index + 1}",
                RequiredAccountKey = target.AccountKey,
                RequiredCharacterKey = target.CharacterKey,
                WakePolicy = DadSchedulerWakePolicy.LaunchIfOffline,
            }).ToList(),
        };

        currentState.Slots = BuildSlotStates(group, pool, [], allowRosterMaintenanceTarget: true);
        InitializeWakeTimestamps(currentState.Slots, currentState.StartedAtUtc);
        var blockers = currentState.Slots
            .Select(static slot => slot.BlockedReason)
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockers.Count > 0)
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = string.Join(" | ", blockers);
            currentState.BlockedReason = currentState.Summary;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return;
        }

        if (job.DryRun)
        {
            currentState.Phase = DadSchedulerPresetPhase.Completed;
            currentState.Summary = $"Roster update dry run ready for {targets.Count} character(s).";
            currentState.CompletedAtUtc = DateTime.UtcNow;
            RecordTerminalResult(currentState);
            return;
        }

        currentState.Phase = currentState.Slots.All(static slot => slot.Ready)
            ? DadSchedulerPresetPhase.ReadyToStart
            : ResolveWaitingPhase(currentState.Slots);
        currentState.Summary = $"Roster update waiting: {currentState.Slots.Count(static slot => slot.Ready)}/{currentState.Slots.Count} target(s) ready.";
        nextRefreshUtc = DateTime.MinValue;
    }

    private void UpdateRosterUpdateJob()
    {
        var pool = characterIntelligenceService.RequestPeerSnapshots();
        var group = new DadPlannerGroup
        {
            GroupId = currentState.JobId,
            DisplayName = currentState.PresetName,
            Slots = currentState.Slots.Select(static slot => new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                AllianceAssignment = slot.AllianceAssignment,
                RequiredAccountKey = slot.RequiredAccountKey,
                RequiredCharacterKey = slot.RequiredCharacterKey,
                RequiredJobId = slot.RequiredJobId,
                AdsLootMode = slot.AdsLootMode,
                LevelSeekTarget = slot.LevelSeekTarget,
                WakePolicy = slot.WakePolicy,
                LaunchProfileId = slot.LaunchProfileId,
            }).ToList(),
        };
        currentState.Slots = BuildSlotStates(group, pool, currentState.Slots, allowRosterMaintenanceTarget: true);
        currentState.UpdatedAtUtc = DateTime.UtcNow;

        if (!DadDependencyMutationBoundaryRules.CanCross(
                dependencyGateCommitted,
                currentState.Slots.Select(static slot => slot.DependenciesReady)))
        {
            currentState.Phase = DadSchedulerPresetPhase.WaitingForDependencies;
            currentState.Summary = BuildReadinessWaitSummary(currentState, dependenciesCommitted: false);
            return;
        }
        dependencyGateCommitted = true;

        if (!AdvanceWakeTakeoverPipelines(currentState.Slots, out var barrierBlocker))
        {
            BlockActive(barrierBlocker);
            return;
        }

        foreach (var slot in currentState.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
            {
                BlockActive(slot.BlockedReason);
                return;
            }

            if (IsParticipantReadyTimedOut(slot, out var timeoutReason))
            {
                currentState.Phase = DadSchedulerPresetPhase.TimedOut;
                currentState.BlockedReason = timeoutReason;
                currentState.Summary = timeoutReason;
                currentState.CompletedAtUtc = DateTime.UtcNow;
                currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
                RecordTerminalResult(currentState);
                return;
            }

            if (slot.Ready)
                continue;
        }

        if (!currentState.Slots.All(static slot => slot.Ready))
        {
            currentState.Phase = ResolveWaitingPhase(currentState.Slots, dependencyGateCommitted);
            currentState.Summary = $"Roster update waiting: {currentState.Slots.Count(static slot => slot.Ready)}/{currentState.Slots.Count} target(s) ready.";
            return;
        }

        var results = new List<DadRosterRefreshResultDto>();
        foreach (var slot in currentState.Slots)
        {
            var participant = BuildParticipantSet(pool).FirstOrDefault(candidate =>
                string.Equals(candidate.WorkerSessionId.Value, slot.MatchedWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase));
            if (participant == null)
            {
                BlockActive($"Roster update lost participant for {slot.RequiredCharacterKey}.");
                return;
            }

            var command = new DadRosterRefreshCommandDto
            {
                CommandId = $"{currentState.JobId}:{slot.SlotId}:roster-refresh",
                AccountKey = slot.RequiredAccountKey,
                CharacterKey = slot.RequiredCharacterKey,
                ContentId = ResolveSlotContentId(slot),
                SaveAfterRefresh = true,
            };

            var result = participant.IsLocalClient
                ? rosterCatalogService.RefreshLocalRosterCharacter(command, presenceService.BuildSnapshotCopy())
                : transportService.SendRosterRefreshCommand(participant, command);
            if (result == null)
            {
                currentState.Phase = DadSchedulerPresetPhase.Resolving;
                currentState.Summary = $"Roster update awaiting acknowledgement from {slot.RequiredCharacterKey}.";
                currentState.UpdatedAtUtc = DateTime.UtcNow;
                return;
            }

            if (!participant.IsLocalClient)
                rosterCatalogService.RecordRefreshResult(result);
            results.Add(result);
            if (!result.Success)
            {
                BlockActive(result.Summary);
                return;
            }
        }

        currentState.Phase = DadSchedulerPresetPhase.Completed;
        currentState.Summary = $"Roster update completed for {results.Count} character(s).";
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        RecordTerminalResult(currentState);
        characterIntelligenceService.RefreshLocalCharacterPool("roster-update", logRefresh: false);
    }

    private void UpdateMapCrewJob(Func<string, DadPlannerGroup?> groupResolver)
    {
        var group = groupResolver(currentState.GroupId);
        if (group == null)
        {
            BlockActive($"Map crew preset '{currentState.GroupId}' could not be resolved.");
            return;
        }

        var pool = characterIntelligenceService.RequestPeerSnapshots();
        currentState.Slots = BuildSlotStates(group, pool, currentState.Slots);
        currentState.UpdatedAtUtc = DateTime.UtcNow;

        if (!DadDependencyMutationBoundaryRules.CanCross(
                dependencyGateCommitted,
                currentState.Slots.Select(static slot => slot.DependenciesReady)))
        {
            currentState.Phase = DadSchedulerPresetPhase.WaitingForDependencies;
            currentState.Summary = BuildReadinessWaitSummary(currentState, dependenciesCommitted: false);
            return;
        }
        dependencyGateCommitted = true;

        if (!AdvanceWakeTakeoverPipelines(currentState.Slots, out var barrierBlocker))
        {
            BlockActive(barrierBlocker);
            return;
        }

        foreach (var slot in currentState.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
            {
                BlockActive(slot.BlockedReason);
                return;
            }

            if (IsParticipantReadyTimedOut(slot, out var timeoutReason))
            {
                currentState.Phase = DadSchedulerPresetPhase.TimedOut;
                currentState.BlockedReason = timeoutReason;
                currentState.Summary = timeoutReason;
                currentState.CompletedAtUtc = DateTime.UtcNow;
                currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
                RecordTerminalResult(currentState);
                return;
            }

            if (slot.Ready)
                continue;
        }

        if (!currentState.Slots.All(static slot => slot.Ready))
        {
            currentState.Phase = ResolveWaitingPhase(currentState.Slots, dependencyGateCommitted);
            currentState.Summary = BuildMapCrewSummary(
                activeJob,
                group,
                $"{currentState.Slots.Count(static slot => slot.Ready)}/{currentState.Slots.Count} slot(s) ready");
            return;
        }

        currentState.Phase = DadSchedulerPresetPhase.Completed;
        currentState.Summary = BuildMapCrewSummary(activeJob, group, $"manual map crew ready with {currentState.Slots.Count} slot(s)");
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
        RecordTerminalResult(currentState);
    }

    private static DadSchedulerPresetState BuildBlockedQueuedState(DadScheduledCrewJob job, string reason)
        => new()
        {
            SchedulerRunId = Guid.NewGuid().ToString("N"),
            JobId = job.JobId,
            JobType = job.JobType,
            RequestedBy = job.RequestedBy,
            GroupId = job.GroupId,
            PresetName = job.PresetName,
            Phase = DadSchedulerPresetPhase.Blocked,
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow,
            DryRun = job.DryRun,
            Summary = reason,
            BlockedReason = reason,
            ScheduleId = job.ScheduleId,
            ScheduleRunId = job.ScheduleRunId,
            ScheduleEntryId = job.ScheduleEntryId,
            ScheduleEntryIndex = job.ScheduleEntryIndex,
            ScheduleRepeatIteration = job.ScheduleRepeatIteration,
            ScheduleCadence = job.ScheduleCadence,
        };

    private static IReadOnlyList<DadRosterCharacter> ResolveRosterUpdateTargets(
        DadScheduledCrewJob job,
        DadAccountRosterCatalog catalog)
    {
        job.TargetCharacters ??= [];
        job.TargetCharacterKeys ??= [];
        job.TargetAccountKeys ??= [];
        var explicitRefs = job.TargetCharacters
            .Where(static reference => reference is { IsEmpty: false })
            .ToList();
        var explicitTargets = job.TargetCharacterKeys
            .Where(static key => !key.IsEmpty)
            .Select(static key => key.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explicitAccounts = job.TargetAccountKeys
            .Where(static key => !key.IsEmpty)
            .Select(static key => key.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hasExplicitTargets = explicitRefs.Count > 0 || explicitTargets.Count > 0 || explicitAccounts.Count > 0;
        return catalog.Characters
            .Where(character => explicitRefs.Count == 0 ||
                                explicitRefs.Any(reference => DadRosterIdentity.Matches(character, reference)))
            .Where(character => explicitTargets.Count == 0 ||
                                explicitTargets.Contains(character.CharacterKey.Value))
            .Where(character => explicitAccounts.Count == 0 ||
                                explicitAccounts.Contains(character.AccountKey.Value))
            .Where(character => character.NeedsRosterUpdate || hasExplicitTargets)
            .Where(static character => !character.AccountKey.IsEmpty && !character.CharacterKey.IsEmpty)
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<DadSchedulerSlotState> RebuildActiveSlots(
        IReadOnlyList<DadSchedulerSlotState> previousSlots)
    {
        // The heartbeat projection is updated in memory by transport. Revalidation must not add
        // a two-second peer pull or move the normal five-second character/XADB refresh boundary.
        var pool = characterIntelligenceService.CurrentPool;
        var syntheticGroup = new DadPlannerGroup
        {
            GroupId = currentState.GroupId,
            DisplayName = currentState.PresetName,
            Slots = previousSlots.Select(static slot => new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                AllianceAssignment = slot.AllianceAssignment,
                RequiredAccountKey = slot.RequiredAccountKey,
                RequiredCharacterKey = slot.RequiredCharacterKey,
                RequiredJobId = slot.RequiredJobId,
                AdsLootMode = slot.AdsLootMode,
                LevelSeekTarget = slot.LevelSeekTarget,
                WakePolicy = slot.WakePolicy,
                LaunchProfileId = slot.LaunchProfileId,
            }).ToList(),
        };
        var rebuilt = BuildSlotStates(
            syntheticGroup,
            pool,
            previousSlots,
            allowRosterMaintenanceTarget: currentState.JobType == DadSchedulerJobType.RosterUpdate);

        foreach (var slot in rebuilt)
        {
            var previous = previousSlots.FirstOrDefault(existing =>
                string.Equals(existing.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
            if (previous == null)
                continue;

            slot.LaunchStarted = previous.LaunchStarted;
            slot.LaunchStartedUtc = previous.LaunchStartedUtc;
            slot.LoadCommandSentUtc = previous.LoadCommandSentUtc;
            if (!string.IsNullOrWhiteSpace(previous.Summary) && !slot.Ready)
                slot.Summary = previous.Summary;
        }

        return rebuilt;
    }

    private void UpdateDailyRewardPreflight()
    {
        var active = dailyRewardPreflight;
        if (active == null || active.CheckedSlotCount <= 0 || currentState.Slots.Count != active.CheckedSlotCount)
        {
            ContinueAfterDailyRewardPreflight("Daily reward preflight state was unavailable; running the preset normally.");
            return;
        }

        var now = DateTime.UtcNow;
        if (now >= active.SlotDeadlineUtc)
        {
            ContinueAfterDailyRewardPreflight("Daily reward preflight timed out; running the preset normally.");
            return;
        }

        currentState.Slots = RebuildActiveSlots(currentState.Slots);
        currentState.UpdatedAtUtc = now;
        if (active.CurrentSlotIndex < 0 || active.CurrentSlotIndex >= currentState.Slots.Count)
        {
            ContinueAfterDailyRewardPreflight("Daily reward preflight cursor was invalid; running the preset normally.");
            return;
        }

        var slot = currentState.Slots[active.CurrentSlotIndex];
        if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
        {
            ContinueAfterDailyRewardPreflight($"Daily reward preflight could not prove {slot.SlotId}: {slot.BlockedReason} Running the preset normally.");
            return;
        }

        if (!AdvanceWakeTakeoverPipelines([slot], out var takeoverBlocker))
        {
            ContinueAfterDailyRewardPreflight($"Daily reward preflight wake was uncertain for {slot.SlotId}: {takeoverBlocker} Running the preset normally.");
            return;
        }

        if (!slot.Ready)
        {
            currentState.Summary = $"DailyReset reward preflight is waking checked row {active.CurrentSlotIndex + 1}/{active.CheckedSlotCount} ({slot.SlotId}) without touching unchecked rows.";
            return;
        }

        var participants = BuildParticipantSet(characterIntelligenceService.CurrentPool);
        var participant = participants.FirstOrDefault(candidate =>
            !slot.MatchedWorkerSessionId.IsEmpty &&
            string.Equals(candidate.WorkerSessionId.Value, slot.MatchedWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) &&
            (candidate.IsLocalClient || transportService.IsWorkerOnline(candidate.WorkerSessionId)));
        if (participant == null ||
            !participant.WorldReadyStable ||
            !participant.IsAvailable ||
            !DadRosterIdentity.SameAccount(participant.ManagedAccountKey, slot.RequiredAccountKey) ||
            !string.Equals(participant.ActiveCharacterKey.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(participant.Character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            ContinueAfterDailyRewardPreflight($"Daily reward preflight lost the exact world-ready route for {slot.SlotId}; running the preset normally.");
            return;
        }

        if (active.ActiveRequest == null)
        {
            var contentId = ResolveFrozenSlotContentId(slot);
            if (contentId == 0 || participant.Character.ContentId != contentId)
            {
                ContinueAfterDailyRewardPreflight($"Daily reward preflight could not prove the exact Content ID for {slot.SlotId}; running the preset normally.");
                return;
            }

            active.ActiveRequest = new DadRouletteRewardProbeRequestDto
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Operation = DadRouletteRewardProbeOperation.Inspect,
                SchedulerRunId = currentState.SchedulerRunId,
                ScheduleId = currentState.ScheduleId,
                ScheduleRunId = currentState.ScheduleRunId,
                ScheduleEntryId = currentState.ScheduleEntryId,
                SlotId = slot.SlotId,
                RouteWorkerSessionId = participant.WorkerSessionId,
                AccountKey = slot.RequiredAccountKey,
                CharacterKey = slot.RequiredCharacterKey,
                CharacterContentId = contentId,
                RouletteId = active.Target.RouletteId,
                RouletteKey = active.Target.Key,
                RequestedAtUtc = now,
            };
            active.SlotDeadlineUtc = now + ResolveDailyRewardPreflightTimeout();
        }

        var request = active.ActiveRequest;
        if (!string.Equals(request.RouteWorkerSessionId.Value, participant.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ||
            !transportService.IsWorkerOnline(participant.WorkerSessionId))
        {
            ContinueAfterDailyRewardPreflight($"Daily reward preflight route changed for {slot.SlotId}; running the preset normally.");
            return;
        }

        var response = transportService.SendRouletteRewardProbe(participant, request);
        if (response == null)
        {
            currentState.Summary = $"DailyReset reward preflight is waiting for exact reward truth from {slot.SlotId} ({active.CurrentSlotIndex + 1}/{active.CheckedSlotCount}).";
            return;
        }

        if (!DadRouletteRewardProbeIdentityRules.TryValidateResponse(request, response, now, out var responseFailure))
        {
            ContinueAfterDailyRewardPreflight($"Daily reward preflight discarded a stale or contradictory reply for {slot.SlotId}: {responseFailure} Running the preset normally.");
            return;
        }

        if (response.Outcome == DadRouletteRewardProbeOutcome.Pending)
        {
            currentState.Summary = $"DailyReset reward preflight is waiting for stable exact roulette identity and reward counts from {slot.SlotId}.";
            return;
        }

        var receivedCount = active.ReceivedSlotCount +
                            (response.Outcome == DadRouletteRewardProbeOutcome.Received ? 1 : 0);
        var disposition = DadDailyRewardPreflightRules.Resolve(
            active.CheckedSlotCount,
            receivedCount,
            routeAvailable: true,
            timedOut: false,
            response.Outcome);
        if (disposition == DadDailyRewardPreflightDisposition.RunNormally)
        {
            ContinueAfterDailyRewardPreflight($"Daily reward preflight for {slot.SlotId} returned {response.Outcome}: {response.Summary} Running the preset normally.");
            return;
        }

        if (disposition == DadDailyRewardPreflightDisposition.SkipEntry)
        {
            active.ReceivedSlotCount = receivedCount;
            active.Evidence.Add($"{slot.SlotId} {response.ReceivedRewardCount}/{response.MaxRewardCount}");
            SkipForDailyReward(active);
            return;
        }

        if (disposition == DadDailyRewardPreflightDisposition.ContinueToNextCheckedSlot)
        {
            active.ReceivedSlotCount = receivedCount;
            active.Evidence.Add($"{slot.SlotId} {response.ReceivedRewardCount}/{response.MaxRewardCount}");
            active.CurrentSlotIndex++;
            active.ActiveRequest = null;
            active.SlotDeadlineUtc = now + ResolveDailyRewardPreflightTimeout();
            currentState.Summary = $"DailyReset reward preflight proved {active.ReceivedSlotCount}/{active.CheckedSlotCount} checked row(s) received; advancing to the next checked row.";
        }
    }

    private void ContinueAfterDailyRewardPreflight(string reason)
    {
        CancelActiveDailyRewardProbe();
        dailyRewardPreflight = null;
        currentState.Slots = frozenOrdinarySlots.Select(static slot => slot.Clone()).ToList();
        frozenOrdinarySlots = [];
        InitializeWakeTimestamps(currentState.Slots, currentState.StartedAtUtc);
        dependencyGateCommitted = false;
        currentState.Phase = DadSchedulerPresetPhase.Resolving;
        currentState.Summary = reason;
        currentState.BlockedReason = string.Empty;
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        nextRefreshUtc = DateTime.MinValue;
        LogSchedulerPhaseTransition();
    }

    private void SkipForDailyReward(DailyRewardPreflightSession active)
    {
        const string cleanupReason = "DailyReset entry skipped after exact received roulette reward preflight.";
        CancelNonTerminalTakeovers(cleanupReason);
        currentState.Phase = DadSchedulerPresetPhase.Skipped;
        currentState.SkipKind = DadSchedulerSkipKind.DailyRouletteReward;
        currentState.Summary = $"Skipped preset '{currentState.PresetName}': every checked effective row has received Daily Roulette #{active.Target.RouletteId} reward ({string.Join(", ", active.Evidence)}).";
        currentState.BlockedReason = string.Empty;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
        frozenEarlyAssignments = [];
        frozenPlannerRequest = null;
        postWakeLevelTargetsEvaluated = false;
        RecordTerminalResult(currentState);
    }

    private void CancelActiveDailyRewardProbe()
    {
        var request = dailyRewardPreflight?.ActiveRequest;
        if (request == null)
            return;

        var cancel = request.Clone();
        cancel.Operation = DadRouletteRewardProbeOperation.Cancel;
        var participant = new DadParticipantSnapshot
        {
            WorkerSessionId = cancel.RouteWorkerSessionId,
        };
        _ = transportService.SendRouletteRewardProbe(participant, cancel);
    }

    private TimeSpan ResolveDailyRewardPreflightTimeout()
        => TimeSpan.FromSeconds(Math.Clamp(
            configuration.ParticipantReadyTimeoutSeconds <= 0
                ? 300
                : configuration.ParticipantReadyTimeoutSeconds,
            30,
            1800));

    private static DadPlannerGroup BuildEffectiveSchedulerGroup(
        DadPlannerGroup group,
        DadPlannerRunRequestPreview plannerRequestPreview)
    {
        var activityMode = plannerRequestPreview.PlannerPreview.ActivityMode;
        var projected = DadEffectivePlannerGroupProjection.Project(
            group,
            activityMode,
            plannerRequestPreview.ExpectedPartySize);
        return DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(
            projected,
            plannerRequestPreview.PlannerPreview.SelectedCharacters);
    }

    private List<DadSchedulerSlotState> BuildSlotStates(
        DadPlannerGroup group,
        DadCharacterPool pool,
        IReadOnlyList<DadSchedulerSlotState> previousSlots,
        bool allowRosterMaintenanceTarget = false,
        IReadOnlyList<DadLaunchProfile>? launchProfiles = null)
    {
        var participants = BuildParticipantSet(pool);
        return group.Slots.Select(slot =>
        {
            var previous = previousSlots.FirstOrDefault(existing =>
                string.Equals(existing.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
            var state = BuildSlotState(
                slot,
                participants,
                allowRosterMaintenanceTarget,
                previous?.MatchedWorkerSessionId ?? new DadWorkerSessionId(string.Empty),
                launchProfiles);
            if (previous != null)
            {
                var rebound = !previous.MatchedWorkerSessionId.IsEmpty &&
                              !state.MatchedWorkerSessionId.IsEmpty &&
                              !string.Equals(
                                  previous.MatchedWorkerSessionId.Value,
                                  state.MatchedWorkerSessionId.Value,
                                  StringComparison.OrdinalIgnoreCase);
                if (rebound)
                {
                    QueueReboundWorkerCleanup(previous);
                    log.Information(
                        "[dad] Scheduler safely rebound sole stable-account route request={RequestId} slot={SlotId} account={AccountKey} oldWorker={OldWorkerSessionId} newWorker={NewWorkerSessionId}.",
                        currentState.PlannerRequestId,
                        state.SlotId,
                        state.RequiredAccountKey,
                        previous.MatchedWorkerSessionId,
                        state.MatchedWorkerSessionId);
                }
                else
                {
                    CopyWakeRuntimeState(previous, state);
                }
            }

            ApplyWakePolicyState(state, currentState.IsActive && dependencyGateCommitted);

            return state;
        }).ToList();
    }

    private DadSchedulerSlotState BuildSlotState(
        DadPlannerGroupSlot slot,
        IReadOnlyList<DadParticipantSnapshot> participants,
        bool allowRosterMaintenanceTarget = false,
        DadWorkerSessionId frozenWorkerSessionId = default,
        IReadOnlyList<DadLaunchProfile>? launchProfiles = null)
    {
        var state = new DadSchedulerSlotState
        {
            SlotId = string.IsNullOrWhiteSpace(slot.SlotId) ? "Slot" : slot.SlotId,
            AllianceAssignment = slot.AllianceAssignment,
            WakePolicy = slot.WakePolicy,
            RequiredAccountKey = slot.RequiredAccountKey,
            RequiredCharacterKey = slot.RequiredCharacterKey,
            RequiredJobId = slot.RequiredJobId,
            AdsLootMode = slot.AdsLootMode,
            LevelSeekTarget = slot.LevelSeekTarget,
            LaunchProfileId = slot.LaunchProfileId?.Trim() ?? string.Empty,
            MatchedWorkerSessionId = frozenWorkerSessionId,
        };
        state.RosterVisibility = rosterCatalogService.ResolveVisibility(slot.RequiredCharacterKey, slot.RequiredAccountKey);
        state.NeedsRosterUpdate = rosterCatalogService.ResolveNeedsRosterUpdate(slot.RequiredCharacterKey, slot.RequiredAccountKey);
        if (!allowRosterMaintenanceTarget &&
            (state.RosterVisibility is DadRosterVisibility.Hidden or DadRosterVisibility.Ignored ||
             state.NeedsRosterUpdate))
        {
            var rosterState = state.NeedsRosterUpdate
                ? "NeedsRosterUpdate"
                : state.RosterVisibility.ToString();
            state.BlockedReason = $"Slot {state.SlotId} targets {rosterState} roster character {slot.RequiredCharacterKey}.";
        }

        var frozenObserved = frozenWorkerSessionId.IsEmpty
            ? null
            : participants.FirstOrDefault(participant =>
                string.Equals(participant.WorkerSessionId.Value, frozenWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) &&
                (participant.IsLocalClient || transportService.IsWorkerOnline(participant.WorkerSessionId)));
        var matchingAccount = !frozenWorkerSessionId.IsEmpty
            ? DadSchedulerRoutingRules.ResolveCurrentOrSoleReconnectedClient(
                  slot.RequiredAccountKey,
                  frozenWorkerSessionId,
                  participants,
                  transportService.IsWorkerOnline)
            : slot.RequiredAccountKey.IsEmpty
            ? participants.FirstOrDefault(participant =>
                participant.IsAvailable && MatchesSlotCharacter(participant, slot))
            : DadSchedulerRoutingRules.ResolveExactConnectedClient(
                slot.RequiredAccountKey,
                participants,
                transportService.IsWorkerOnline);
        var matchingCharacter = matchingAccount is { IsAvailable: true } &&
                                MatchesSlotCharacter(matchingAccount, slot)
            ? matchingAccount
            : null;
        var selected = matchingAccount;
        var contradictionEvidence = string.Empty;
        var contradictionStable = false;
        if (frozenObserved != null &&
            !slot.RequiredAccountKey.IsEmpty &&
            !frozenObserved.ManagedAccountKey.IsEmpty &&
            !DadRosterIdentity.SameAccount(frozenObserved.ManagedAccountKey, slot.RequiredAccountKey))
        {
            contradictionStable = frozenObserved.WorldReadyStable;
            contradictionEvidence = $"Frozen worker {frozenWorkerSessionId} reports account {frozenObserved.ManagedAccountKey}; expected {slot.RequiredAccountKey}.";
        }
        else if (selected != null &&
                 MatchesSlotCharacter(selected, slot) &&
                 selected.Character.ContentId != 0)
        {
            var expectedContentId = ResolveFrozenSlotContentId(state);
            if (expectedContentId != 0 && selected.Character.ContentId != expectedContentId)
            {
                contradictionStable = selected.WorldReadyStable;
                contradictionEvidence = $"Character {slot.RequiredCharacterKey} reports Content ID {selected.Character.ContentId}; expected frozen {expectedContentId}.";
            }
        }

        var contradiction = GetSlotContradictionTracker(state.SlotId).Observe(
            contradictionEvidence,
            contradictionStable,
            DateTime.UtcNow,
            RefreshInterval,
            (frozenObserved ?? selected)?.LastHeartbeatUtc);
        if (!string.IsNullOrWhiteSpace(contradictionEvidence))
        {
            state.MatchedWorkerSessionId = frozenObserved?.WorkerSessionId ?? selected?.WorkerSessionId ?? frozenWorkerSessionId;
            state.ClientConnected = frozenObserved != null || selected != null;
            state.IsOnline = frozenObserved?.IsAvailable ?? selected?.IsAvailable ?? false;
            state.ActiveCharacterKey = frozenObserved?.ActiveCharacterKey ?? selected?.ActiveCharacterKey ?? new DadCharacterKey(string.Empty);
            state.ExternalAutomationHeld = true;
            state.ExternalAutomationActivity = "IdentityConfirmation";
            state.ExternalAutomationSummary = contradiction.Summary;
            state.Summary = contradiction.Summary;
            if (contradiction.Disposition == DadSafetyProofDisposition.Reject)
                state.BlockedReason = contradiction.Summary;
            selected = null;
            matchingCharacter = null;
        }

        if (selected != null)
        {
            state.ClientConnected = true;
            state.IsOnline = selected.IsAvailable;
            state.CorrectCharacter = matchingCharacter != null || slot.RequiredCharacterKey.IsEmpty;
            state.PostArReady = selected.PostArReady;
            state.BasePostArReady = selected.WorldReadyStable;
            state.AutoRetainerAvailable = selected.AutoRetainerAvailable;
            state.AutoRetainerBusy = selected.AutoRetainerBusy;
            state.MultiModeEnabled = selected.AutoRetainerMultiModeEnabled;
            state.ExternalAutomationHeld = selected.ExternalAutomationHeld;
            state.ExternalAutomationActivity = selected.ExternalAutomationActivity;
            state.ExternalAutomationState = selected.ExternalAutomationState;
            state.ExternalAutomationSummary = selected.ExternalAutomationSummary;
            state.MatchedWorkerSessionId = selected.WorkerSessionId;
            state.ActiveCharacterKey = selected.ActiveCharacterKey;
            var dependencyGate = DadDependencyGateRules.EvaluateParticipant(
                selected,
                DateTime.UtcNow,
                TimeSpan.FromSeconds(Math.Max(3, configuration.HeartbeatStaleSeconds)),
                $"Slot {state.SlotId}");
            state.DependenciesReady = dependencyGate.Ready;
            state.DependencyState = dependencyGate.State;
            state.DependencyRevision = dependencyGate.Revision;
            state.DependencySummary = dependencyGate.Summary;
            state.Summary = !state.IsOnline
                ? $"Same-account Dad client connected as worker {selected.WorkerSessionId}; character offline or relogging."
                : state.CorrectCharacter
                ? selected.PostArReady
                    ? $"Online and post-AR ready on {selected.ActiveCharacterKey}."
                    : $"Online on {selected.ActiveCharacterKey}; waiting for post-AR readiness."
                : $"Account online as {selected.ActiveCharacterKey}; needs {slot.RequiredCharacterKey}.";
        }
        else
        {
            state.DependenciesReady = false;
            state.DependencyState = DadDependencyState.Checking;
            state.DependencySummary = $"Slot {state.SlotId} is absent. Start that game client manually so DAD can check its required plugins.";
            state.Summary = $"Slot {state.SlotId} is waiting for the same-account Dad client.";
        }

        ApplyLaunchProfileState(state, slot, launchProfiles);
        return state;
    }

    private void ApplyLaunchProfileState(
        DadSchedulerSlotState state,
        DadPlannerGroupSlot slot,
        IReadOnlyList<DadLaunchProfile>? launchProfiles = null)
    {
        var profile = ResolveLaunchProfile(slot, launchProfiles);
        if (profile == null)
            return;

        profile.Normalize();
        state.LaunchProfileId = profile.ProfileId;
        state.LaunchProfileName = profile.DisplayName;
        state.BatchPath = profile.BatchPath;
        state.LaunchProfileDryRun = profile.DryRun;
    }

    private static void ApplyWakePolicyState(DadSchedulerSlotState state, bool dependenciesCommitted)
    {
        if (!string.IsNullOrWhiteSpace(state.BlockedReason))
            return;

        if (!dependenciesCommitted && !state.DependenciesReady)
        {
            state.Ready = false;
            state.Summary = state.DependencySummary;
            return;
        }

        if (state.ExternalAutomationHeld)
        {
            state.Ready = false;
            state.PostArReady = false;
            state.TakeoverStatus = DadWakeTakeoverStatus.Pending;
            state.TakeoverStage = DadWakeTakeoverStage.WaitingForExternalAutomation;
            var detail = string.Join(
                "/",
                new[] { state.ExternalAutomationActivity, state.ExternalAutomationState }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
            state.Summary = string.IsNullOrWhiteSpace(detail)
                ? "Waiting for VERMAXION status."
                : $"Waiting for VERMAXION — {detail}.";
            if (!string.IsNullOrWhiteSpace(state.ExternalAutomationSummary))
                state.Summary += $" {state.ExternalAutomationSummary}";
            return;
        }

        var decision = DadWakePolicyRules.Evaluate(
            state.WakePolicy,
            state.ClientConnected,
            state.CorrectCharacter,
            state.PostArReady,
            state.TakeoverStatus,
            state.BlockedReason,
            state.AutoRetainerAvailable,
            state.AutoRetainerBusy,
            state.MultiModeEnabled);
        state.Ready = decision.Ready;
        if (state.WakePolicy != DadSchedulerWakePolicy.LaunchIfOffline || state.TakeoverStage == DadWakeTakeoverStage.None)
            state.TakeoverStage = decision.Stage;

        if (!string.IsNullOrWhiteSpace(decision.BlockedReason))
        {
            state.BlockedReason = $"Slot {state.SlotId}: {decision.BlockedReason}";
            state.Summary = state.BlockedReason;
        }
        else if (state.WakePolicy != DadSchedulerWakePolicy.LaunchIfOffline || string.IsNullOrWhiteSpace(state.Summary))
        {
            state.Summary = decision.Summary;
        }
    }

    private static void CopyWakeRuntimeState(DadSchedulerSlotState source, DadSchedulerSlotState target)
    {
        target.MatchedWorkerSessionId = DadSchedulerRoutingRules.PreserveFrozenWorkerSession(
            source.MatchedWorkerSessionId,
            target.MatchedWorkerSessionId);
        target.LaunchStarted = source.LaunchStarted;
        target.LaunchStartedUtc = source.LaunchStartedUtc;
        target.LoadCommandSentUtc = source.LoadCommandSentUtc;
        target.TakeoverStatus = source.TakeoverStatus;
        target.TakeoverStage = source.TakeoverStage;
        target.TakeoverPhase = source.TakeoverPhase;
        target.OperationToken = source.OperationToken;
        target.CommitKind = source.CommitKind;
        target.CommitExecutionUtc = source.CommitExecutionUtc;
        target.AcknowledgementState = source.AcknowledgementState;
        target.TakeoverRequestedUtc = source.TakeoverRequestedUtc;
        target.ResetIssuedUtc = source.ResetIssuedUtc;
        target.ResetExecutionUtc = source.ResetExecutionUtc;
        target.TakeoverVerifiedUtc = source.TakeoverVerifiedUtc;
        target.RelogIssuedUtc = source.RelogIssuedUtc;
        target.RelogExecutionUtc = source.RelogExecutionUtc;
        target.ReadyUtc = source.ReadyUtc;
        var hasAuthoritativeReservation = !string.IsNullOrWhiteSpace(source.OperationToken) &&
                                           source.VermaxionReservationState != DadVermaxionReservationState.NotLoaded &&
                                           source.VermaxionReservationState != DadVermaxionReservationState.Rejected &&
                                           source.VermaxionReservationState != DadVermaxionReservationState.Released;
        if (hasAuthoritativeReservation)
        {
            target.VermaxionReservationState = source.VermaxionReservationState;
            target.VermaxionReservationSummary = source.VermaxionReservationSummary;
            target.VermaxionReservationCreatedAtUtc = source.VermaxionReservationCreatedAtUtc;
            target.VermaxionReservationUpdatedAtUtc = source.VermaxionReservationUpdatedAtUtc;
            target.ExternalAutomationHeld = source.VermaxionReservationState != DadVermaxionReservationState.Granted &&
                                                !IsCompatibilityHandoff(
                                                    source.VermaxionReservationState,
                                                    source.ExternalAutomationActivity,
                                                    source.TakeoverPhase);
            target.ExternalAutomationActivity = source.ExternalAutomationActivity;
            target.ExternalAutomationState = source.ExternalAutomationState;
            target.ExternalAutomationSummary = source.ExternalAutomationSummary;
            target.PostArReady = target.BasePostArReady && !target.ExternalAutomationHeld;
        }
        target.RelogIssued = source.RelogIssued;
        target.VermaxionHoldStartedUtc = source.VermaxionHoldStartedUtc;
        target.AutoRetainerWaitStartedUtc = source.AutoRetainerWaitStartedUtc;
        target.ParticipantWaitStartedUtc = source.ParticipantWaitStartedUtc;
        target.TimeoutStage = source.TimeoutStage;
        target.TimeoutStageObservedUtc = source.TimeoutStageObservedUtc;
        target.VermaxionHoldElapsedSeconds = source.VermaxionHoldElapsedSeconds;
        target.AutoRetainerWaitElapsedSeconds = source.AutoRetainerWaitElapsedSeconds;
        target.ParticipantWaitElapsedSeconds = source.ParticipantWaitElapsedSeconds;
        if (!string.IsNullOrWhiteSpace(source.Summary))
            target.Summary = source.Summary;
    }

    private DadStableContradictionTracker GetSlotContradictionTracker(string slotId)
    {
        if (!slotContradictionTrackers.TryGetValue(slotId, out var tracker))
        {
            tracker = new DadStableContradictionTracker();
            slotContradictionTrackers[slotId] = tracker;
        }

        return tracker;
    }

    private void QueueReboundWorkerCleanup(DadSchedulerSlotState previous)
    {
        if (string.IsNullOrWhiteSpace(currentState.SchedulerRunId) || previous.MatchedWorkerSessionId.IsEmpty)
            return;

        var cleanupJob = activeJob?.Clone() ?? new DadScheduledCrewJob
        {
            JobId = currentState.JobId,
            GroupId = currentState.GroupId,
            PresetName = currentState.PresetName,
            RequestedBy = currentState.RequestedBy,
        };
        const string reason = "Stable-account route reconnected on a new worker session; retaining exact old-route cleanup while the sole replacement safely rebinds.";
        if (DadSchedulerRoutingRules.RequiresTakeoverCancellation(previous))
        {
            var takeoverKey = $"{currentState.SchedulerRunId}|{previous.SlotId}|{previous.MatchedWorkerSessionId.Value}";
            pendingTakeoverCancellations[takeoverKey] = new PendingTakeoverCancellation
            {
                SchedulerRunId = currentState.SchedulerRunId,
                Job = cleanupJob.Clone(),
                Slot = previous.Clone(),
                Reason = reason,
                RequestedAtUtc = previous.TakeoverRequestedUtc ?? currentState.StartedAtUtc,
                BlocksFutureWork = false,
            };
        }

        foreach (var assignment in frozenEarlyAssignments.Where(assignment =>
                     string.Equals(assignment.Request.AssignedSlotId, previous.SlotId, StringComparison.OrdinalIgnoreCase) &&
                     assignment.AttemptedWorkerSessionIds.Contains(previous.MatchedWorkerSessionId.Value)))
        {
            var assignmentKey = $"{assignment.Request.RunId}|{assignment.Request.AssignedSlotId}|{previous.MatchedWorkerSessionId.Value}";
            pendingEarlyAssignmentCancellations[assignmentKey] = new PendingEarlyAssignmentCancellation
            {
                RunId = assignment.Request.RunId,
                WorkerSessionId = previous.MatchedWorkerSessionId,
                Job = cleanupJob.Clone(),
                SlotId = assignment.Request.AssignedSlotId,
                Reason = reason,
                BlocksFutureWork = false,
            };
        }
    }

    private IReadOnlyList<DadParticipantSnapshot> BuildParticipantSet(DadCharacterPool pool)
    {
        var participants = new List<DadParticipantSnapshot> { presenceService.BuildLiveSafetySnapshot() };
        participants.AddRange(pool.PeerTransport.KnownParticipants.Select(static participant => participant.Clone()));
        return participants
            .Where(static participant =>
                !participant.WorkerSessionId.IsEmpty &&
                participant.State != DadParticipantState.Stale)
            .DistinctBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ulong ResolveSlotContentId(DadSchedulerSlotState slot)
    {
        var character = rosterCatalogService.FindCharacter(new DadRosterCharacterRef
        {
            AccountKey = slot.RequiredAccountKey,
            CharacterKey = slot.RequiredCharacterKey,
        });
        return character?.ContentId ?? 0;
    }

    private ulong ResolveFrozenSlotContentId(DadSchedulerSlotState slot)
    {
        if (currentState.IsActive && frozenPlannerRequest != null)
        {
            var assignment = frozenEarlyAssignments.FirstOrDefault(candidate =>
                string.Equals(candidate.Request.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase) &&
                DadRosterIdentity.SameAccount(candidate.Request.RequiredAccountKey, slot.RequiredAccountKey) &&
                string.Equals(
                    candidate.Request.RequiredCharacterKey.Value,
                    slot.RequiredCharacterKey.Value,
                    StringComparison.OrdinalIgnoreCase));
            if (assignment?.Request.RequiredContentId > 0)
                return assignment.Request.RequiredContentId;

            var frozenReference = frozenPlannerRequest.Orchestration?.RequiredRosterCharacters?.FirstOrDefault(candidate =>
                DadRosterIdentity.SameAccount(candidate.AccountKey, slot.RequiredAccountKey) &&
                string.Equals(candidate.CharacterKey.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase));
            if (frozenReference?.ContentId > 0)
                return frozenReference.ContentId;
        }

        return ResolveSlotContentId(slot);
    }

    private DadLaunchProfile? ResolveLaunchProfile(
        DadPlannerGroupSlot slot,
        IReadOnlyList<DadLaunchProfile>? launchProfiles = null)
    {
        if (launchProfiles == null)
        {
            NormalizeLaunchProfiles();
            launchProfiles = configuration.LaunchProfiles;
        }

        if (!string.IsNullOrWhiteSpace(slot.LaunchProfileId))
        {
            var exact = launchProfiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, slot.LaunchProfileId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
        }

        var primaryLaunchProfileId = ResolvePrimaryLaunchProfileId(slot.RequiredAccountKey);
        if (!string.IsNullOrWhiteSpace(primaryLaunchProfileId))
        {
            var primary = launchProfiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, primaryLaunchProfileId, StringComparison.OrdinalIgnoreCase) &&
                (profile.AccountKey.IsEmpty || DadRosterIdentity.SameAccount(profile.AccountKey, slot.RequiredAccountKey)));
            if (primary != null)
                return primary;
        }

        return launchProfiles.FirstOrDefault(profile =>
                   !slot.RequiredCharacterKey.IsEmpty &&
                   (slot.RequiredAccountKey.IsEmpty ||
                    profile.AccountKey.IsEmpty ||
                    DadRosterIdentity.SameAccount(profile.AccountKey, slot.RequiredAccountKey)) &&
                   profile.ExpectedCharacterKeys.Any(key =>
                       string.Equals(key.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase)));
    }

    private string ResolvePrimaryLaunchProfileId(DadAccountKey accountKey)
    {
        if (accountKey.IsEmpty)
            return string.Empty;

        var local = configManager.GetAccount(accountKey);
        if (local != null && !string.IsNullOrWhiteSpace(local.PrimaryLaunchProfileId))
            return local.PrimaryLaunchProfileId;

        return profileDirectoryService.GetCatalogs()
            .SelectMany(static catalog => catalog.Accounts)
            .FirstOrDefault(account => DadRosterIdentity.SameAccount(account.AccountKey, accountKey))
            ?.PrimaryLaunchProfileId ?? string.Empty;
    }

    private static void InitializeWakeTimestamps(
        IEnumerable<DadSchedulerSlotState> slots,
        DateTime startedAtUtc)
    {
        foreach (var slot in slots.Where(static slot => slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline))
            slot.TakeoverRequestedUtc ??= startedAtUtc;
    }

    private bool AdvanceWakeTakeoverPipelines(IReadOnlyList<DadSchedulerSlotState> slots, out string blocker)
    {
        blocker = string.Empty;
        var preexistingBlocker = slots.Select(static slot => slot.BlockedReason)
            .FirstOrDefault(static reason => !string.IsNullOrWhiteSpace(reason));
        if (!string.IsNullOrWhiteSpace(preexistingBlocker))
        {
            blocker = preexistingBlocker;
            return false;
        }
        var takeoverSlots = slots.Where(static slot => slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline).ToList();
        if (takeoverSlots.Count == 0)
            return true;

        var participants = BuildParticipantSet(characterIntelligenceService.CurrentPool);
        var routes = new Dictionary<string, DadParticipantSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in takeoverSlots)
        {
            if (!TryResolveTakeoverParticipant(slot, participants, out var participant))
                continue;

            routes[slot.SlotId] = participant!;
            LogTakeoverTransition(slot, "connected/routable");
        }

        for (var index = 0; index < takeoverSlots.Count; index++)
        {
            var slot = takeoverSlots[index];
            if (!routes.TryGetValue(slot.SlotId, out var participant))
                continue;

            var earlierSameWorker = takeoverSlots
                .Take(index)
                .FirstOrDefault(candidate =>
                    !candidate.Ready &&
                    !candidate.MatchedWorkerSessionId.IsEmpty &&
                    string.Equals(
                        candidate.MatchedWorkerSessionId.Value,
                        slot.MatchedWorkerSessionId.Value,
                        StringComparison.OrdinalIgnoreCase));
            if (earlierSameWorker != null)
            {
                slot.Ready = false;
                slot.Summary = $"Frozen worker {slot.MatchedWorkerSessionId} is finishing {earlierSameWorker.SlotId} before {slot.SlotId}.";
                LogTakeoverTransition(slot, "waiting-for-same-worker-slot");
                continue;
            }

            slot.TakeoverRequestedUtc ??= currentState.StartedAtUtc;
            slot.OperationToken = currentState.SchedulerRunId;
            var decision = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slot, DateTime.UtcNow);
            if (!decision.CanDispatch)
                continue;
            if (decision.CommitKind == DadWakeCommitKind.None &&
                slot.NextTakeoverStatusCheckUtc.HasValue &&
                DateTime.UtcNow < slot.NextTakeoverStatusCheckUtc.Value)
            {
                continue;
            }

            StoreSlotExecutionBoundary(slot, decision);
            var result = SendWakeTakeover(
                slot,
                participant,
                decision.MessageKind,
                decision.CommitKind,
                decision.ExecutionTimeUtc);
            if (result == null)
            {
                ClearUnacknowledgedExecutionBoundary(slot, decision.CommitKind);
                continue;
            }
            ApplyWakeTakeoverResult(slot, result, participant);
            if (result.Status == DadWakeTakeoverStatus.Blocked)
            {
                blocker = string.IsNullOrWhiteSpace(result.BlockedReason) ? result.Summary : result.BlockedReason;
                return false;
            }

            var followThrough = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slot, DateTime.UtcNow);
            if (!followThrough.CanDispatch || followThrough.CommitKind == DadWakeCommitKind.None)
                continue;

            StoreSlotExecutionBoundary(slot, followThrough);
            result = SendWakeTakeover(
                slot,
                participant,
                followThrough.MessageKind,
                followThrough.CommitKind,
                followThrough.ExecutionTimeUtc);
            if (result == null)
            {
                ClearUnacknowledgedExecutionBoundary(slot, followThrough.CommitKind);
                continue;
            }
            ApplyWakeTakeoverResult(slot, result, participant);
            if (result.Status == DadWakeTakeoverStatus.Blocked)
            {
                blocker = result.BlockedReason;
                return false;
            }
        }

        return true;
    }

    private void StoreSlotExecutionBoundary(
        DadSchedulerSlotState slot,
        DadWakeSlotPipelineDecision decision)
    {
        if (!decision.ExecutionTimeUtc.HasValue)
            return;

        if (decision.CommitKind == DadWakeCommitKind.Reset)
        {
            slot.ResetExecutionUtc ??= decision.ExecutionTimeUtc;
            currentState.ResetExecutionUtc ??= slot.ResetExecutionUtc;
        }
        else if (decision.CommitKind == DadWakeCommitKind.Relog)
        {
            slot.RelogExecutionUtc ??= decision.ExecutionTimeUtc;
            currentState.RelogExecutionUtc ??= slot.RelogExecutionUtc;
        }
    }

    private static void ClearUnacknowledgedExecutionBoundary(
        DadSchedulerSlotState slot,
        DadWakeCommitKind commitKind)
    {
        // The worker may have committed despite a lost response; its operation token remains the
        // authority in that case. Clearing only the coordinator's unacknowledged proposal ensures
        // a genuinely missed GO is retried with a fresh future boundary instead of a past timestamp.
        if (commitKind == DadWakeCommitKind.Reset)
            slot.ResetExecutionUtc = null;
        else if (commitKind == DadWakeCommitKind.Relog)
            slot.RelogExecutionUtc = null;
    }

    private bool TryResolveTakeoverParticipant(
        DadSchedulerSlotState slot,
        IReadOnlyList<DadParticipantSnapshot> participants,
        out DadParticipantSnapshot? participant)
    {
        participant = slot.MatchedWorkerSessionId.IsEmpty
            ? DadSchedulerRoutingRules.ResolveExactConnectedClient(
                slot.RequiredAccountKey,
                participants,
                transportService.IsWorkerOnline)
            : DadSchedulerRoutingRules.ResolveFrozenConnectedClient(
                slot.RequiredAccountKey,
                slot.MatchedWorkerSessionId,
                participants,
                transportService.IsWorkerOnline);
        if (participant == null)
        {
            MarkTakeoverRouteUnavailable(slot, "missing-or-unroutable");
            return false;
        }

        ApplyConnectedRoute(slot, participant);
        return true;
    }

    private DadWakeTakeoverResultDto? SendWakeTakeover(
        DadSchedulerSlotState slot,
        DadParticipantSnapshot participant,
        DadWakeTakeoverMessageKind kind,
        DadWakeCommitKind commitKind,
        DateTime? executionTimeUtc)
    {
        var request = new DadWakeTakeoverRequestDto
        {
            SchedulerRunId = currentState.SchedulerRunId,
            OperationToken = currentState.SchedulerRunId,
            SlotId = slot.SlotId,
            AccountKey = slot.RequiredAccountKey,
            CharacterKey = slot.RequiredCharacterKey,
            RequestedAtUtc = slot.TakeoverRequestedUtc ?? currentState.StartedAtUtc,
            MessageKind = kind,
            CommitKind = commitKind,
            ExecutionTimeUtc = executionTimeUtc,
        };
        if (!participant.IsLocalClient && !transportService.IsWorkerOnline(participant.WorkerSessionId))
        {
            MarkTakeoverRouteUnavailable(slot, "offline-before-dispatch");
            return null;
        }

        var result = participant.IsLocalClient
            ? wakeTakeoverService.Handle(request)
            : transportService.SendWakeTakeoverRequest(participant, request);
        if (kind is DadWakeTakeoverMessageKind.Prepare or DadWakeTakeoverMessageKind.Status)
            slot.NextTakeoverStatusCheckUtc = DateTime.UtcNow + TakeoverStatusInterval;
        if (result == null)
        {
            slot.Ready = false;
            slot.Summary = $"Wake takeover {kind}/{commitKind} submitted for {slot.RequiredCharacterKey}; awaiting acknowledgement.";
            LogTakeoverTransition(slot, "submitted/awaiting-ack");
        }
        return result;
    }

    private void ApplyWakeTakeoverResult(
        DadSchedulerSlotState slot,
        DadWakeTakeoverResultDto result,
        DadParticipantSnapshot latestHeartbeat)
    {
        var previousPhase = slot.TakeoverPhase;
        var resetAuthorizationInvalidated =
            (previousPhase is DadWakeTakeoverPhase.Prepared or DadWakeTakeoverPhase.ResetCommitted) &&
            (result.Phase < DadWakeTakeoverPhase.Prepared ||
             result.AcknowledgementState == DadWakeAcknowledgementState.Rejected);
        slot.TakeoverStatus = result.Status;
        slot.TakeoverStage = result.Stage;
        slot.TakeoverPhase = result.Phase;
        slot.OperationToken = result.OperationToken;
        slot.CommitKind = result.CommitKind;
        slot.CommitExecutionUtc = result.ExecutionTimeUtc;
        slot.AcknowledgementState = result.AcknowledgementState;
        slot.ResetIssuedUtc = result.ResetIssuedUtc;
        slot.TakeoverVerifiedUtc = result.TakeoverVerifiedUtc;
        slot.RelogIssuedUtc = result.RelogIssuedUtc;
        slot.ReadyUtc = result.ReadyUtc;
        slot.PostArReady = result.PostArReady;
        slot.AutoRetainerAvailable = result.AutoRetainerAvailable;
        slot.AutoRetainerBusy = result.AutoRetainerBusy;
        slot.MultiModeEnabled = result.MultiModeEnabled;
        slot.RelogIssued = result.RelogIssued;
        slot.ExternalAutomationHeld = result.ExternalAutomationHeld;
        slot.ExternalAutomationActivity = result.ExternalAutomationActivity;
        slot.ExternalAutomationState = result.ExternalAutomationState;
        slot.ExternalAutomationSummary = result.ExternalAutomationSummary;
        slot.VermaxionReservationState = result.VermaxionReservationState;
        slot.VermaxionReservationSummary = result.VermaxionReservationSummary;
        slot.VermaxionReservationCreatedAtUtc = result.VermaxionReservationCreatedAtUtc;
        slot.VermaxionReservationUpdatedAtUtc = result.VermaxionReservationUpdatedAtUtc;
        slot.Summary = result.Summary;
        slot.BlockedReason = result.Status == DadWakeTakeoverStatus.Blocked
            ? result.BlockedReason
            : string.Empty;
        if (resetAuthorizationInvalidated)
        {
            // A compatibility client yielded at its local reset boundary. Only that slot needs a
            // fresh five-second GO; the readable global compatibility timestamps remain historical.
            slot.ResetExecutionUtc = null;
            slot.RelogExecutionUtc = null;
        }

        ApplyConnectedRoute(slot, latestHeartbeat);
        if (DadSchedulerRoutingRules.IsExactFrozenTakeoverSnapshot(slot, result.Snapshot))
            ApplyConnectedRoute(slot, result.Snapshot);
        slot.Ready = DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(slot, result) &&
                     slot.CorrectCharacter &&
                     slot.BasePostArReady;
        if (result.Status == DadWakeTakeoverStatus.Ready && !slot.Ready)
        {
            slot.Summary = $"Takeover completed previously, but exact world-ready proof is not current: target {slot.RequiredCharacterKey}, active {slot.ActiveCharacterKey}, exact match {slot.CorrectCharacter}, world ready {slot.BasePostArReady}, PostAr diagnostic {slot.PostArReady}, Multi Mode {(slot.MultiModeEnabled ? "on" : "off")}.";
        }
        LogTakeoverTransition(slot, "acknowledged");
    }

    private static void ApplyConnectedRoute(
        DadSchedulerSlotState slot,
        DadParticipantSnapshot participant)
    {
        if (!slot.MatchedWorkerSessionId.IsEmpty &&
            !string.Equals(
                slot.MatchedWorkerSessionId.Value,
                participant.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        slot.ClientConnected = true;
        slot.IsOnline = participant.IsAvailable;
        slot.MatchedWorkerSessionId = participant.WorkerSessionId;
        slot.ActiveCharacterKey = participant.ActiveCharacterKey;
        slot.CorrectCharacter = participant.IsAvailable &&
                                !slot.RequiredCharacterKey.IsEmpty &&
                                string.Equals(
                                    participant.ActiveCharacterKey.Value,
                                    slot.RequiredCharacterKey.Value,
                                    StringComparison.OrdinalIgnoreCase);
        slot.BasePostArReady = participant.WorldReadyStable;
        slot.AutoRetainerAvailable = participant.AutoRetainerAvailable;
        slot.AutoRetainerBusy = participant.AutoRetainerBusy;
        slot.MultiModeEnabled = participant.AutoRetainerMultiModeEnabled;
        var authoritativeReservation = !string.IsNullOrWhiteSpace(slot.OperationToken) &&
                                       slot.VermaxionReservationState != DadVermaxionReservationState.NotLoaded &&
                                       slot.VermaxionReservationState != DadVermaxionReservationState.Rejected &&
                                       slot.VermaxionReservationState != DadVermaxionReservationState.Released;
        if (authoritativeReservation)
        {
            slot.ExternalAutomationHeld = slot.VermaxionReservationState != DadVermaxionReservationState.Granted &&
                                              !IsCompatibilityHandoff(
                                                  slot.VermaxionReservationState,
                                                  slot.ExternalAutomationActivity,
                                                  slot.TakeoverPhase);
            slot.PostArReady = participant.WorldReadyStable && !slot.ExternalAutomationHeld;
        }
        else
        {
            slot.PostArReady = participant.PostArReady;
            slot.ExternalAutomationHeld = participant.ExternalAutomationHeld;
            slot.ExternalAutomationActivity = participant.ExternalAutomationActivity;
            slot.ExternalAutomationState = participant.ExternalAutomationState;
            slot.ExternalAutomationSummary = participant.ExternalAutomationSummary;
        }
    }

    private void MarkTakeoverRouteUnavailable(DadSchedulerSlotState slot, string routeStatus)
    {
        slot.ClientConnected = false;
        slot.IsOnline = false;
        slot.CorrectCharacter = false;
        slot.Ready = false;
        slot.TakeoverStatus = DadWakeTakeoverStatus.Pending;
        slot.TakeoverStage = DadWakeTakeoverStage.WaitingForClient;
        slot.Summary = $"Waiting for the exact same-account Dad client route for {slot.RequiredAccountKey}; no takeover request was submitted.";
        LogTakeoverTransition(slot, routeStatus);
    }

    private void LogTakeoverTransition(
        DadSchedulerSlotState slot,
        string routeStatus,
        string cleanupResult = "not-requested")
    {
        var runId = string.IsNullOrWhiteSpace(currentState.SchedulerRunId)
            ? "(none)"
            : currentState.SchedulerRunId;
        var worker = slot.MatchedWorkerSessionId.IsEmpty
            ? "(none)"
            : slot.MatchedWorkerSessionId.Value;
        var stableRouteStatus = routeStatus is "submitted/awaiting-ack" or "acknowledged"
            ? "status"
            : routeStatus;
        var transition = string.Join(
            "|",
            stableRouteStatus,
            slot.TakeoverStatus,
            slot.TakeoverStage,
            slot.TakeoverPhase,
            slot.AcknowledgementState,
            slot.RequiredCharacterKey.Value,
            slot.ActiveCharacterKey.Value,
            slot.CorrectCharacter,
            slot.VermaxionReservationState,
            slot.ExternalAutomationActivity,
            slot.ExternalAutomationState,
            slot.AutoRetainerBusy,
            slot.MultiModeEnabled,
            cleanupResult);
        var category = !string.Equals(cleanupResult, "not-requested", StringComparison.Ordinal)
            ? "cleanup"
            : routeStatus is "submitted/awaiting-ack" or "acknowledged"
                ? "takeover"
                : "route";
        var key = $"{runId}|{slot.SlotId}|{category}";
        if (takeoverDiagnosticStates.TryGetValue(key, out var prior) &&
            string.Equals(prior, transition, StringComparison.Ordinal))
        {
            return;
        }

        takeoverDiagnosticStates[key] = transition;
        var preparationAuthority = slot.TakeoverPhase < DadWakeTakeoverPhase.Prepared ||
                                   slot.TakeoverPhase is DadWakeTakeoverPhase.Blocked or DadWakeTakeoverPhase.Cancelled
            ? "not-prepared"
            : IsCompatibilityHandoff(
                slot.VermaxionReservationState,
                slot.ExternalAutomationActivity,
                slot.TakeoverPhase)
                ? "verified-idle-compatibility"
                : slot.VermaxionReservationState == DadVermaxionReservationState.Granted
                    ? "v2-grant"
                    : "character-postprocess";
        log.Information(
            "[dad] Wake route transition: request={SchedulerRunId}, module={ModuleId}, slot={SlotId}, stableAccount={AccountKey}, target={CharacterKey}, contentId={ContentId}, active={ActiveCharacterKey}, exactMatch={ExactCharacterMatch}, worker={WorkerSessionId}, route={RouteStatus}, phase={TakeoverPhase}, stage={TakeoverStage}, ack={AcknowledgementState}, preparation={PreparationAuthority}, reservation={ReservationState}, VERMAXION={VermaxionActivity}/{VermaxionState}, AR={AutoRetainerState}, multiMode={MultiModeState}, cleanup={CleanupResult}.",
            runId,
            activeSchedulerModuleId,
            slot.SlotId,
            slot.RequiredAccountKey,
            slot.RequiredCharacterKey,
            ResolveFrozenSlotContentId(slot),
            slot.ActiveCharacterKey.IsEmpty ? "(none)" : slot.ActiveCharacterKey.Value,
            slot.CorrectCharacter,
            worker,
            routeStatus,
            slot.TakeoverPhase,
            slot.TakeoverStage,
            slot.AcknowledgementState,
            preparationAuthority,
            slot.VermaxionReservationState,
            string.IsNullOrWhiteSpace(slot.ExternalAutomationActivity) ? "(none)" : slot.ExternalAutomationActivity,
            string.IsNullOrWhiteSpace(slot.ExternalAutomationState) ? "(none)" : slot.ExternalAutomationState,
            slot.AutoRetainerBusy ? "busy" : "idle",
            slot.MultiModeEnabled ? "on" : "off",
            cleanupResult);
    }

    private void LogStrictRevalidationDiagnostic(DadStrictPlannerRevalidationDecision decision)
    {
        if (!strictRevalidationTracker.TryRecordDiagnostic(decision.Disposition))
            return;

        if (decision.Disposition == DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness)
        {
            log.Information(
                "[dad] Scheduler {SchedulerRunId} is waiting on transient strict-runtime readiness and remains schedulable: {Reason}",
                currentState.SchedulerRunId,
                decision.Reason);
            return;
        }

        log.Warning(
            "[dad] Scheduler {SchedulerRunId} received a terminal strict planner rejection: {Reason}",
            currentState.SchedulerRunId,
            decision.Reason);
    }

    private void LogSchedulerPhaseTransition()
    {
        if (lastLoggedSchedulerPhase == currentState.Phase)
            return;

        lastLoggedSchedulerPhase = currentState.Phase;
        var requestId = string.IsNullOrWhiteSpace(currentState.PlannerRequestId)
            ? currentState.SchedulerRunId
            : currentState.PlannerRequestId;
        if (currentState.Slots.Count == 0)
        {
            log.Information(
                "[dad] Scheduler phase transition request={RequestId} module={ModuleId} slot=(none) account=(none) character=(none) contentId=0 worker=(none) phase={Phase} summary={Summary}.",
                requestId,
                activeSchedulerModuleId,
                currentState.Phase,
                currentState.Summary);
            return;
        }

        foreach (var slot in currentState.Slots)
        {
            log.Information(
                "[dad] Scheduler phase transition request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} phase={Phase} summary={Summary}.",
                requestId,
                activeSchedulerModuleId,
                slot.SlotId,
                slot.RequiredAccountKey,
                slot.RequiredCharacterKey,
                ResolveFrozenSlotContentId(slot),
                slot.MatchedWorkerSessionId.IsEmpty ? "(none)" : slot.MatchedWorkerSessionId.Value,
                currentState.Phase,
                currentState.Summary);
        }
    }

    private static bool IsCompatibilityHandoff(
        DadVermaxionReservationState reservationState,
        string externalAutomationActivity,
        DadWakeTakeoverPhase phase)
        => reservationState == DadVermaxionReservationState.Unavailable &&
           phase is >= DadWakeTakeoverPhase.Prepared and <= DadWakeTakeoverPhase.Ready &&
           string.Equals(externalAutomationActivity, "CompatibilityHandoff", StringComparison.OrdinalIgnoreCase);

    private bool IsParticipantReadyTimedOut(DadSchedulerSlotState slot, out string reason)
    {
        reason = string.Empty;
        if (slot.Ready)
            return false;
        if (!dependencyGateCommitted && !slot.DependenciesReady)
            return false;

        var now = DateTime.UtcNow;
        DadWakeStageTimeoutPolicy.Observe(slot, now);

        // Character ownership handoff is a logical wait, not a duration budget. Disconnects,
        // VERMAXION/AR work, and missed character boundaries remain pending until explicit cancel.
        if (slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline)
        {
            return false;
        }

        if (!DadWakeStageTimeoutPolicy.IsTimedOut(
                slot,
                now,
                configuration.VermaxionHoldTimeoutSeconds,
                configuration.AutoRetainerBusyTimeoutSeconds,
                configuration.ParticipantReadyTimeoutSeconds))
        {
            return false;
        }

        var budget = DadWakeStageTimeoutPolicy.GetBudgetSeconds(
            slot.TimeoutStage,
            configuration.VermaxionHoldTimeoutSeconds,
            configuration.AutoRetainerBusyTimeoutSeconds,
            configuration.ParticipantReadyTimeoutSeconds);
        var stage = slot.TimeoutStage switch
        {
            DadWakeTimeoutStage.Vermaxion => "VERMAXION hold",
            DadWakeTimeoutStage.AutoRetainer => "AutoRetainer busy",
            _ => "participant readiness",
        };
        var commandEvidence = slot.ResetIssuedUtc.HasValue || slot.RelogIssued
            ? "Takeover commands had already been issued before this stage was observed."
            : "No AutoRetainer, reset, or relog command was issued.";
        reason = $"{stage} timeout for slot {slot.SlotId} after {budget} seconds; " +
                 $"account {slot.RequiredAccountKey}, character {slot.RequiredCharacterKey}, takeover stage {slot.TakeoverStage}. {commandEvidence}";
        CancelNonTerminalTakeovers(reason);
        return true;
    }

    private static DadSchedulerPresetPhase ResolveWaitingPhase(
        IReadOnlyList<DadSchedulerSlotState> slots,
        bool dependenciesCommitted = false)
    {
        if (!dependenciesCommitted && !AllSlotDependenciesReady(slots))
            return DadSchedulerPresetPhase.WaitingForDependencies;

        if (slots.Any(static slot =>
                !slot.Ready &&
                slot.TakeoverStage is DadWakeTakeoverStage.RelogIssued
                    or DadWakeTakeoverStage.WaitingForCharacter
                    or DadWakeTakeoverStage.WaitingForPostArReady
                    or DadWakeTakeoverStage.WaitingForExternalAutomation))
            return DadSchedulerPresetPhase.LoadingCharacters;

        if (slots.Any(static slot => slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline && !slot.Ready))
            return DadSchedulerPresetPhase.WaitingForHeartbeat;

        return DadSchedulerPresetPhase.Resolving;
    }

    private static string BuildReadinessWaitSummary(
        DadSchedulerPresetState state,
        bool dependenciesCommitted = false)
    {
        var waiting = state.Slots.Where(static slot => !slot.Ready).ToList();
        var detail = waiting
            .Select(slot => !dependenciesCommitted && !slot.DependenciesReady && !string.IsNullOrWhiteSpace(slot.DependencySummary)
                ? $"{slot.SlotId}: {slot.DependencySummary}"
                : string.IsNullOrWhiteSpace(slot.Summary)
                ? $"{slot.SlotId}: waiting for {slot.RequiredCharacterKey}"
                : $"{slot.SlotId}: {slot.Summary}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unbounded = waiting.Any(static slot => slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline);
        var waitPolicy = unbounded
            ? $" {FormatElapsed(DateTime.UtcNow - state.StartedAtUtc)} elapsed; no timeout; cancel to stop."
            : string.Empty;
        var reasons = detail.Count == 0 ? string.Empty : $" {string.Join(" | ", detail)}";
        return $"Scheduler waiting: {state.Slots.Count(static slot => slot.Ready)}/{state.Slots.Count} slot(s) ready.{waitPolicy}{reasons}";
    }

    private static bool AllSlotDependenciesReady(IEnumerable<DadSchedulerSlotState> slots)
        => slots.All(static slot => slot.DependenciesReady);

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static bool MatchesSlotAccount(DadParticipantSnapshot participant, DadPlannerGroupSlot slot)
    {
        if (slot.RequiredAccountKey.IsEmpty)
            return !slot.RequiredCharacterKey.IsEmpty && MatchesSlotCharacter(participant, slot);

        return string.Equals(participant.ManagedAccountKey.Value, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase)
               || string.Equals(participant.ManagedAccountAlias, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase)
               || string.Equals(participant.Character.AccountId, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase)
               || string.Equals(participant.Character.AccountAlias, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSlotCharacter(DadParticipantSnapshot participant, DadPlannerGroupSlot slot)
    {
        if (slot.RequiredCharacterKey.IsEmpty)
            return false;

        return string.Equals(participant.ActiveCharacterKey.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase)
               || string.Equals(participant.Character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static void BlockPreview(DadSchedulerPreview preview, string reason)
    {
        preview.CanStart = false;
        preview.ReadyToStart = false;
        preview.StatusSummary = string.IsNullOrWhiteSpace(reason) ? "Scheduler preview blocked." : reason;
        preview.BlockedReason = preview.StatusSummary;
        preview.Phase = DadSchedulerPresetPhase.Blocked;
    }

    private void BlockActive(string reason)
    {
        dailyRewardPreflight = null;
        frozenOrdinarySlots = [];
        currentState.Phase = DadSchedulerPresetPhase.Blocked;
        currentState.Summary = string.IsNullOrWhiteSpace(reason) ? "Scheduler blocked." : reason;
        currentState.BlockedReason = currentState.Summary;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        if (IsCrewFormationActive)
        {
            FinishCrewFormation(DadCrewFormationPhase.Blocked, currentState.Summary);
            return;
        }
        CancelNonTerminalTakeovers(reason);
        RecordTerminalResult(currentState);
    }

    private void CancelNonTerminalTakeovers(string reason)
    {
        CancelActiveDailyRewardProbe();
        if (string.IsNullOrWhiteSpace(currentState.SchedulerRunId))
            return;

        var cleanupJob = activeJob?.Clone() ?? new DadScheduledCrewJob
        {
            JobId = currentState.JobId,
            JobType = currentState.JobType,
            GroupId = currentState.GroupId,
            PresetName = currentState.PresetName,
            RequestedBy = currentState.RequestedBy,
            CreatedAtUtc = currentState.StartedAtUtc,
        };
        cleanupJob.StatusSummary = $"Cancellation cleanup pending for scheduler Job ID {cleanupJob.JobId}.";
        cleanupJob.BlockedReason = string.Empty;

        QueueEarlyAssignmentCancellations(cleanupJob, reason);

        foreach (var slot in currentState.Slots.Where(DadSchedulerRoutingRules.RequiresTakeoverCancellation))
        {
            var key = $"{currentState.SchedulerRunId}|{slot.SlotId}";
            pendingTakeoverCancellations[key] = new PendingTakeoverCancellation
            {
                SchedulerRunId = currentState.SchedulerRunId,
                Job = cleanupJob.Clone(),
                Slot = slot.Clone(),
                Reason = string.IsNullOrWhiteSpace(reason) ? "Scheduler cancelled." : reason.Trim(),
                RequestedAtUtc = slot.TakeoverRequestedUtc ?? currentState.StartedAtUtc,
            };
            LogTakeoverTransition(slot, "cleanup-queued", "cancel-pending-route-or-ack");
        }

        UpdatePendingTakeoverCancellations();
        UpdatePendingEarlyAssignmentCancellations();
    }

    private void QueueEarlyAssignmentCancellations(DadScheduledCrewJob cleanupJob, string reason)
    {
        foreach (var assignment in frozenEarlyAssignments)
        {
            foreach (var workerId in assignment.AttemptedWorkerSessionIds)
            {
                var key = $"{assignment.Request.RunId}|{assignment.Request.AssignedSlotId}|{workerId}";
                pendingEarlyAssignmentCancellations[key] = new PendingEarlyAssignmentCancellation
                {
                    RunId = assignment.Request.RunId,
                    WorkerSessionId = new DadWorkerSessionId(workerId),
                    Job = cleanupJob.Clone(),
                    SlotId = assignment.Request.AssignedSlotId,
                    Reason = string.IsNullOrWhiteSpace(reason) ? "Scheduler cancelled." : reason.Trim(),
                };
            }
        }
    }

    private void UpdateCompletedPlannerAssignmentCleanup(DadRunResult visibleRun)
    {
        if (currentState.Phase != DadSchedulerPresetPhase.StartedPlanner ||
            frozenPlannerRequest == null ||
            frozenEarlyAssignments.Count == 0 ||
            !visibleRun.IsTerminal ||
            !string.Equals(visibleRun.RequestId, frozenPlannerRequest.RequestId, StringComparison.Ordinal))
        {
            return;
        }

        var cleanupJob = activeJob?.Clone() ?? new DadScheduledCrewJob
        {
            JobId = currentState.JobId,
            JobType = currentState.JobType,
            GroupId = currentState.GroupId,
            PresetName = currentState.PresetName,
            RequestedBy = currentState.RequestedBy,
            CreatedAtUtc = currentState.StartedAtUtc,
        };
        var reason = $"Dad run {visibleRun.RequestId} reached terminal status {visibleRun.Status}; clearing every early requested-job assignment route.";
        QueueEarlyAssignmentCancellations(cleanupJob, reason);
        log.Information(
            "[dad] Queued terminal early-assignment cleanup request={RequestId} status={Status} attemptedRoutes={AttemptedRouteCount}.",
            visibleRun.RequestId,
            visibleRun.Status,
            frozenEarlyAssignments.Sum(static assignment => assignment.AttemptedWorkerSessionIds.Count));
        frozenEarlyAssignments = [];
        frozenPlannerRequest = null;
        postWakeLevelTargetsEvaluated = false;
        UpdatePendingEarlyAssignmentCancellations();
    }

    private DadScheduledCrewJob? FindEquivalentActiveOrPendingJob(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        return DadSchedulerSubmissionRules.FindDuplicate(
            groupId,
            levelingOperation?.OuterJob ?? activeJob,
            IsSchedulerActive,
            configuration.SchedulerQueue,
            PendingCleanupJobs())
            ?.Job;
    }

    internal DadScheduledCrewJob? GetPendingTakeoverCleanupJob(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        return PendingCleanupJobs()
            .FirstOrDefault(job => string.Equals(job.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            ?.Clone();
    }

    internal bool IsPendingTakeoverCleanupJob(string jobId)
        => !string.IsNullOrWhiteSpace(jobId) &&
           PendingCleanupJobs().Any(job =>
               string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase));

    private bool HasPendingCleanup
        => pendingTakeoverCancellations.Values.Any(static pending => pending.BlocksFutureWork) ||
           pendingEarlyAssignmentCancellations.Values.Any(static pending => pending.BlocksFutureWork);

    private bool HasActiveRebindCleanup
        => pendingTakeoverCancellations.Values.Any(pending =>
               pending.BlocksFutureWork &&
               string.Equals(pending.SchedulerRunId, currentState.SchedulerRunId, StringComparison.Ordinal)) ||
           (frozenPlannerRequest != null && pendingEarlyAssignmentCancellations.Values.Any(pending =>
               pending.BlocksFutureWork &&
               string.Equals(pending.RunId, frozenPlannerRequest.RequestId, StringComparison.Ordinal)));

    private IEnumerable<DadScheduledCrewJob> PendingCleanupJobs()
        => pendingTakeoverCancellations.Values
            .Where(static pending => pending.BlocksFutureWork)
            .Select(static pending => pending.Job)
            .Concat(pendingEarlyAssignmentCancellations.Values
                .Where(static pending => pending.BlocksFutureWork)
                .Select(static pending => pending.Job));

    private static DadRunRequest? CloneRunRequest(DadRunRequest? request)
        => request == null
            ? null
            : DadIpcJson.Deserialize<DadRunRequest>(DadIpcJson.Serialize(request));

    private DadScheduleRepeatBoundary ResolveScheduleRepeatBoundary()
    {
        var schedule = FindSchedule(currentState.ScheduleId);
        return DadScheduleRules.ResolveRepeatBoundary(
            currentState.ScheduleRunId,
            currentState.ScheduleEntryId,
            currentState.ScheduleRepeatIteration,
            schedule?.Entries);
    }

    private List<FrozenEarlyAssignment> BuildFrozenEarlyAssignments(
        DadRunRequest request,
        IReadOnlyList<DadSchedulerSlotState> slots,
        DadScheduledCrewJob job)
    {
        return DadEarlyRequestedJobAssignmentRules.Build(
                request,
                slots,
                presenceService.WorkerSessionId,
                ResolveSlotContentId)
            .Select(assignment => new FrozenEarlyAssignment
            {
                CommandId = $"job:{request.RequestId}:{assignment.AssignedSlotId}:{assignment.RequiredAccountKey.Value}:{assignment.RequiredCharacterKey.Value}",
                Job = job.Clone(),
                Request = assignment,
            })
            .ToList();
    }

    private static string ValidateFrozenEarlyAssignments(
        DadRunRequest? request,
        IReadOnlyList<DadSchedulerSlotState> slots,
        IReadOnlyList<FrozenEarlyAssignment> assignments)
    {
        if (request == null || request.Orchestration == null)
            return "Scheduler admission did not produce a frozen planner request and orchestration intent.";

        var requestedSlots = slots.Where(static slot => slot.RequiredJobId.HasValue).ToList();
        if (assignments.Count != requestedSlots.Count)
            return $"Frozen requested-job manifest contains {assignments.Count}/{requestedSlots.Count} required slot assignments.";

        foreach (var slot in requestedSlots)
        {
            var matches = assignments.Where(candidate =>
                string.Equals(candidate.Request.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                return $"Frozen requested-job manifest requires exactly one assignment for {slot.SlotId}; found {matches.Count}.";

            var assignment = matches[0].Request;
            if (!string.Equals(assignment.RunId, request.RequestId, StringComparison.Ordinal) ||
                assignment.AuthorityMode != request.Orchestration.AuthorityMode ||
                assignment.ModuleId != request.Orchestration.ModuleTarget ||
                !DadRosterIdentity.SameAccount(assignment.RequiredAccountKey, slot.RequiredAccountKey) ||
                !string.Equals(assignment.RequiredCharacterKey.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
                assignment.RequiredContentId == 0 ||
                assignment.RequiredJobId != slot.RequiredJobId)
            {
                return $"Frozen requested-job assignment for {slot.SlotId} is missing or contradicts its immutable request/account/character/Content ID/job identity.";
            }
        }

        return string.Empty;
    }

    private static string DescribeQueueTarget(DadRunRequest request)
    {
        var target = request.DailyMsq?.QueueTarget ??
                     request.Commendation?.QueueTarget ??
                     request.Astrope?.QueueTarget ??
                     request.CustomDuty?.QueueTarget;
        if (target != null)
            return $"{target.Kind}:{target.ContentFinderConditionId}:{target.RouletteId}:{target.Key}";
        if (request.PremadeDuty != null)
            return $"{DadQueueTargetKind.DutyFinderDuty}:{request.PremadeDuty.ContentFinderConditionId}:0";
        return "none";
    }

    private static string BuildQueuedJobSummary(DadScheduledCrewJob job)
        => job.JobType switch
        {
            DadSchedulerJobType.MapCrew => $"Queued map crew '{job.PresetName}' ({job.MapMode}{FormatMapTemplateSuffix(job.MapRunTemplate)}).",
            DadSchedulerJobType.RosterUpdate => string.IsNullOrWhiteSpace(job.StatusSummary)
                ? "Queued roster update."
                : job.StatusSummary,
            _ => $"Queued preset '{job.PresetName}'.",
        };

    private static string FirstNonEmptyOrEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? "Scheduler start was rejected.";

    private static string BuildMapCrewSummary(DadScheduledCrewJob? job, DadPlannerGroup group, string status)
    {
        var mode = job?.MapMode ?? group.MapMode;
        var template = string.IsNullOrWhiteSpace(job?.MapRunTemplate)
            ? group.MapRunTemplate
            : job.MapRunTemplate;
        return $"Map crew '{group.DisplayName}' {status}. Mode {mode}{FormatMapTemplateSuffix(template)}.";
    }

    private static string FormatMapTemplateSuffix(string template)
        => string.IsNullOrWhiteSpace(template) ? string.Empty : $", template '{template.Trim()}'";

    private static string BuildUnsupportedMapCrewBlocker(DadMapCrewJobMode mapMode)
        => mapMode switch
        {
            DadMapCrewJobMode.GatherThenRun => "Map crew GatherThenRun is blocked: missing map inventory/runner IPC contract.",
            DadMapCrewJobMode.PluginHandoff => "Map crew PluginHandoff is blocked: missing map inventory/runner IPC contract.",
            _ => string.Empty,
        };

    private static string FormatScheduleText(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static int ResolveScheduleCadenceHours(int cadenceHours)
        => Math.Clamp(cadenceHours <= 0 ? DefaultScheduleCadenceHours : cadenceHours, 1, 24 * 30);

    private static bool IsAllowedBootBatchPath(string batchPath, string bootDirectory)
    {
        if (string.IsNullOrWhiteSpace(batchPath) ||
            string.IsNullOrWhiteSpace(bootDirectory) ||
            !string.Equals(Path.GetExtension(batchPath), ".bat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(bootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(batchPath);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTerminalPhase(DadSchedulerPresetPhase phase)
        => phase is DadSchedulerPresetPhase.StartedPlanner
            or DadSchedulerPresetPhase.Completed
            or DadSchedulerPresetPhase.Blocked
            or DadSchedulerPresetPhase.TimedOut
            or DadSchedulerPresetPhase.Cancelled
            or DadSchedulerPresetPhase.Skipped;

    private static bool IsSuccessfulTerminalPhase(DadSchedulerPresetPhase phase)
        => phase is DadSchedulerPresetPhase.StartedPlanner or DadSchedulerPresetPhase.Completed or DadSchedulerPresetPhase.Skipped;

    private void UpdateActiveScheduleRun(DadRunResult visibleRun)
    {
        NormalizeSchedules();
        NormalizeScheduleHistory();
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        if (!configuration.ActiveScheduleRun.IsActive)
            return;

        var now = DateTime.UtcNow;
        var state = configuration.ActiveScheduleRun.Clone();
        var schedule = FindSchedule(state.ScheduleId);
        if (schedule == null)
        {
            BlockActiveScheduleRun(
                $"Schedule '{state.ScheduleName}' could not be resolved.",
                DadScheduleFailureKind.SchedulerStateDisappeared);
            return;
        }

        if (state.ScheduleRevisionAtStart <= 0 || schedule.Revision != state.ScheduleRevisionAtStart)
        {
            BlockActiveScheduleRun(
                $"Schedule '{state.ScheduleName}' revision changed from {state.ScheduleRevisionAtStart} to {schedule.Revision} while the run was active.",
                DadScheduleFailureKind.ScheduleRevisionChanged);
            return;
        }

        var currentEntry = DadScheduleRules.GetCurrentEntry(state, schedule.Entries);
        if (currentEntry == null ||
            !string.Equals(currentEntry.EntryId, state.CurrentEntryId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentEntry.GroupId, state.CurrentGroupId, StringComparison.OrdinalIgnoreCase))
        {
            BlockActiveScheduleRun(
                $"Schedule '{state.ScheduleName}' entry identity changed at cursor {state.CurrentEntryIndex + 1}.",
                DadScheduleFailureKind.EntryIdentityChanged);
            return;
        }

        if (DadScheduleRules.UpdateOwnedDailyResetBoundary(schedule, state, now))
        {
            configuration.ActiveScheduleRun = state;
            configuration.Save();
        }

        if (TryProcessWaitingDadRun(schedule, visibleRun, now))
            return;

        state = configuration.ActiveScheduleRun.Clone();
        if (!string.IsNullOrWhiteSpace(state.ActiveSchedulerJobId))
        {
            ProcessScheduleSchedulerJob(schedule, state, visibleRun, now);
            return;
        }

        MaterializeScheduleEntryJob(schedule, state, now);
    }

    private bool TryProcessWaitingDadRun(DadScheduleDefinition schedule, DadRunResult visibleRun, DateTime now)
    {
        var state = configuration.ActiveScheduleRun ?? new DadScheduleRunState();
        if (state.Phase != DadScheduleRunPhase.WaitingForDadRun)
            return false;

        var run = ResolveScheduleDadRunResult(state.ActivePlannerRequestId, visibleRun);
        if (run == null || !run.IsTerminal)
        {
            state.Summary = string.IsNullOrWhiteSpace(state.ActivePlannerRequestId)
                ? $"Schedule '{state.ScheduleName}' is waiting for Dad run completion."
                : $"Schedule '{state.ScheduleName}' is waiting for Dad run {state.ActivePlannerRequestId}.";
            state.UpdatedAtUtc = now;
            configuration.ActiveScheduleRun = state;
            return true;
        }

        if (run.Status != DadRunStatus.Completed)
        {
            var detail = string.IsNullOrWhiteSpace(run.FailureReason)
                ? FormatScheduleText(run.Summary, run.Status.ToString())
                : run.FailureReason;
            var failureKind = run.ScheduleFailureKind != DadScheduleFailureKind.None
                ? run.ScheduleFailureKind
                : run.Status == DadRunStatus.Cancelled
                    ? DadScheduleFailureKind.Cancellation
                    : run.Status == DadRunStatus.Rejected
                        ? DadScheduleFailureKind.PreStartRejected
                        : DadScheduleFailureKind.EntryTerminalFailure;
            BlockActiveScheduleRun(
                $"Schedule '{state.ScheduleName}' stopped after '{state.CurrentPresetName}' ended with {run.Status}: {detail}",
                failureKind);
            return true;
        }

        var advanced = DadScheduleRules.AdvanceAfterEntry(
            state,
            schedule.Entries,
            entrySucceeded: true,
            terminalSummary: $"Schedule entry '{state.CurrentPresetName}' completed.",
            now);
        configuration.ActiveScheduleRun = advanced;
        if (advanced.Status == DadScheduleRunStatus.Completed)
            FinalizeScheduleRun(advanced);
        else
            configuration.Save();
        return true;
    }

    private void ProcessScheduleSchedulerJob(
        DadScheduleDefinition schedule,
        DadScheduleRunState state,
        DadRunResult visibleRun,
        DateTime now)
    {
        var result = FindSchedulerResult(state.ActiveSchedulerJobId);
        if (result != null)
        {
            if (!result.Success)
            {
                BlockActiveScheduleRun(
                    $"Schedule '{state.ScheduleName}' stopped: {FormatScheduleText(result.BlockedReason, result.Summary)}",
                    result.FinalPhase == DadSchedulerPresetPhase.Cancelled
                        ? DadScheduleFailureKind.Cancellation
                        : DadScheduleFailureKind.PreStartRejected);
                return;
            }

            if (result.FinalPhase == DadSchedulerPresetPhase.Skipped)
            {
                var advanced = DadScheduleRules.AdvanceAfterEntry(
                    state,
                    schedule.Entries,
                    entrySucceeded: true,
                    terminalSummary: result.SkipKind == DadSchedulerSkipKind.DailyRouletteReward
                        ? $"Schedule entry '{state.CurrentPresetName}' was skipped because every checked effective row already received the selected Daily Roulette reward."
                        : $"Schedule entry '{state.CurrentPresetName}' was skipped because every targeted row already met its level target.",
                    now,
                    entrySkipped: true);
                configuration.ActiveScheduleRun = advanced;
                if (advanced.Status == DadScheduleRunStatus.Completed)
                    FinalizeScheduleRun(advanced);
                else
                    configuration.Save();
                return;
            }

            if (result.JobType == DadSchedulerJobType.LevelingOperation &&
                result.FinalPhase == DadSchedulerPresetPhase.Completed)
            {
                var advanced = DadScheduleRules.AdvanceAfterEntry(
                    state,
                    schedule.Entries,
                    entrySucceeded: true,
                    terminalSummary: $"Schedule Leveling Mode entry '{state.CurrentPresetName}' completed its full job-rotation plan.",
                    now);
                configuration.ActiveScheduleRun = advanced;
                if (advanced.Status == DadScheduleRunStatus.Completed)
                    FinalizeScheduleRun(advanced);
                else
                    configuration.Save();
                return;
            }

            if (string.IsNullOrWhiteSpace(state.ActivePlannerRequestId) &&
                string.Equals(currentState.JobId, state.ActiveSchedulerJobId, StringComparison.OrdinalIgnoreCase))
            {
                state.ActivePlannerRequestId = currentState.PlannerRequestId;
            }

            if (string.IsNullOrWhiteSpace(state.ActivePlannerRequestId))
            {
                BlockActiveScheduleRun(
                    $"Schedule '{state.ScheduleName}' stopped: scheduler job did not produce a Dad run id.",
                    DadScheduleFailureKind.PreStartRejected);
                return;
            }

            state.Phase = DadScheduleRunPhase.WaitingForDadRun;
            state.Summary = $"Schedule '{state.ScheduleName}' waiting for Dad run {state.ActivePlannerRequestId} from '{state.CurrentPresetName}'.";
            state.UpdatedAtUtc = now;
            configuration.ActiveScheduleRun = state;
            configuration.Save();
            TryProcessWaitingDadRun(schedule, visibleRun, now);
            return;
        }

        if (levelingOperation != null &&
            string.Equals(levelingOperation.OuterJob.JobId, state.ActiveSchedulerJobId, StringComparison.OrdinalIgnoreCase))
        {
            var visible = BuildVisibleState();
            state.Phase = DadScheduleRunPhase.WaitingForScheduler;
            state.Summary = $"Schedule '{state.ScheduleName}' waiting for Leveling Mode: {visible.Summary}";
            state.UpdatedAtUtc = now;
            configuration.ActiveScheduleRun = state;
            return;
        }

        if (string.Equals(currentState.JobId, state.ActiveSchedulerJobId, StringComparison.OrdinalIgnoreCase))
        {
            if (currentState.Phase == DadSchedulerPresetPhase.StartedPlanner)
            {
                state.ActivePlannerRequestId = currentState.PlannerRequestId;
                state.Phase = DadScheduleRunPhase.WaitingForDadRun;
                state.Summary = $"Schedule '{state.ScheduleName}' waiting for Dad run {state.ActivePlannerRequestId} from '{state.CurrentPresetName}'.";
                state.UpdatedAtUtc = now;
                configuration.ActiveScheduleRun = state;
                configuration.Save();
                TryProcessWaitingDadRun(schedule, visibleRun, now);
                return;
            }

            if (currentState.Phase is DadSchedulerPresetPhase.Blocked
                or DadSchedulerPresetPhase.TimedOut
                or DadSchedulerPresetPhase.Cancelled)
            {
                BlockActiveScheduleRun(
                    $"Schedule '{state.ScheduleName}' stopped: {FormatScheduleText(currentState.BlockedReason, currentState.Summary)}",
                    currentState.Phase == DadSchedulerPresetPhase.Cancelled
                        ? DadScheduleFailureKind.Cancellation
                        : DadScheduleFailureKind.PreStartRejected);
                return;
            }

            state.Phase = DadScheduleRunPhase.WaitingForScheduler;
            state.Summary = $"Schedule '{state.ScheduleName}' waiting for scheduler job: {currentState.Summary}";
            state.UpdatedAtUtc = now;
            configuration.ActiveScheduleRun = state;
            return;
        }

        if (configuration.SchedulerQueue.Any(job =>
                string.Equals(job.JobId, state.ActiveSchedulerJobId, StringComparison.OrdinalIgnoreCase)))
        {
            state.Phase = DadScheduleRunPhase.WaitingForScheduler;
            state.Summary = $"Schedule '{state.ScheduleName}' queued '{state.CurrentPresetName}' with scheduler job {state.ActiveSchedulerJobId}.";
            state.UpdatedAtUtc = now;
            configuration.ActiveScheduleRun = state;
            return;
        }

        BlockActiveScheduleRun(
            $"Schedule '{state.ScheduleName}' stopped: scheduler job {state.ActiveSchedulerJobId} disappeared before producing a result.",
            DadScheduleFailureKind.SchedulerStateDisappeared);
    }

    private void MaterializeScheduleEntryJob(DadScheduleDefinition schedule, DadScheduleRunState state, DateTime now)
    {
        var groupIds = BuildPlannerGroupIdSet();
        var blocker = DadScheduleRules.ValidateCurrentEntry(state, schedule.Entries, groupIds);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            BlockActiveScheduleRun(blocker, DadScheduleFailureKind.PreStartRejected);
            return;
        }

        var entry = DadScheduleRules.GetCurrentEntry(state, schedule.Entries);
        if (entry == null)
        {
            BlockActiveScheduleRun(
                $"Schedule '{state.ScheduleName}' has no entry at index {state.CurrentEntryIndex + 1}.",
                DadScheduleFailureKind.EntryIdentityChanged);
            return;
        }

        var group = FindPlannerGroup(entry.GroupId);
        if (group == null)
        {
            BlockActiveScheduleRun(
                $"Schedule entry {state.CurrentEntryIndex + 1} references missing preset '{entry.GroupId}'.",
                DadScheduleFailureKind.PreStartRejected);
            return;
        }

        var job = new DadScheduledCrewJob
        {
            JobType = DadSchedulerJobType.ScheduledPreset,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            Enabled = true,
            DryRun = false,
            CreatedAtUtc = now,
            NextEligibleTimeUtc = now,
            RequestedBy = string.IsNullOrWhiteSpace(state.RequestedBy) ? "schedule" : state.RequestedBy,
            Priority = ScheduleJobPriority,
            ScheduleId = schedule.ScheduleId,
            ScheduleRunId = state.RunId,
            ScheduleEntryId = entry.EntryId,
            ScheduleEntryIndex = state.CurrentEntryIndex,
            ScheduleRepeatIteration = state.RepeatIteration,
            ScheduleCadence = schedule.Cadence,
        };
        job.StatusSummary = $"Queued schedule '{schedule.DisplayName}' entry {state.CurrentEntryIndex + 1}, repeat {state.RepeatIteration}/{entry.RepeatCount}: '{group.DisplayName}'.";
        configuration.SchedulerQueue.Add(job);

        state.ActiveSchedulerJobId = job.JobId;
        state.ActivePlannerRequestId = string.Empty;
        state.CurrentGroupId = group.GroupId;
        state.CurrentPresetName = group.DisplayName;
        state.Phase = DadScheduleRunPhase.WaitingForScheduler;
        state.Summary = job.StatusSummary;
        state.UpdatedAtUtc = now;
        configuration.ActiveScheduleRun = state;
        configuration.Save();
    }

    private DadScheduleRunState BeginScheduleRun(
        DadScheduleDefinition schedule,
        bool dryRun,
        bool manualRun,
        string requestedBy,
        DateTime now)
    {
        var state = DadScheduleRules.StartRun(schedule, dryRun, manualRun, requestedBy, now);
        schedule.LastRunStartedAtUtc = now;
        schedule.LastRunStatus = state.Status;
        schedule.LastSummary = state.Summary;
        DadScheduleRules.UpdateOwnedDailyResetBoundary(schedule, state, now);

        if (state.Status == DadScheduleRunStatus.Blocked)
        {
            configuration.ActiveScheduleRun = state;
            FinalizeScheduleRun(state);
            return state;
        }

        if (dryRun)
        {
            var blocker = ValidateWholeSchedule(schedule);
            state = string.IsNullOrWhiteSpace(blocker)
                ? DadScheduleRules.CompleteRun(
                    state,
                    $"Schedule dry run ready: {state.TotalEntryExecutions} preset run(s) across {schedule.Entries.Count} entry/entries.",
                    now)
                : DadScheduleRules.BlockRun(state, blocker, now);
            state.CompletedEntryExecutions = string.IsNullOrWhiteSpace(blocker) ? state.TotalEntryExecutions : 0;
            configuration.ActiveScheduleRun = state;
            FinalizeScheduleRun(state);
            return state;
        }

        configuration.ActiveScheduleRun = state;
        return state;
    }

    private void BlockActiveScheduleRun(
        string reason,
        DadScheduleFailureKind failureKind = DadScheduleFailureKind.Unknown)
    {
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        configuration.ActiveScheduleRun = DadScheduleRules.BlockRun(
            configuration.ActiveScheduleRun,
            reason,
            DateTime.UtcNow,
            failureKind);
        FinalizeScheduleRun(configuration.ActiveScheduleRun);
    }

    private void FinalizeScheduleRun(DadScheduleRunState state)
    {
        var schedule = FindSchedule(state.ScheduleId);
        if (schedule != null)
        {
            schedule.LastRunCompletedAtUtc = state.CompletedAtUtc ?? DateTime.UtcNow;
            schedule.LastRunStatus = state.Status;
            schedule.LastSummary = state.Summary;
            schedule.UpdatedAtUtc = DateTime.UtcNow;
        }

        RecordScheduleRunResult(state.ToResult(state.Status == DadScheduleRunStatus.Completed));
    }

    private void RecordScheduleRunResult(DadScheduleRunResult result)
    {
        NormalizeScheduleHistory();
        if (string.IsNullOrWhiteSpace(result.RunId))
            result.RunId = Guid.NewGuid().ToString("N");

        configuration.ScheduleHistory.RemoveAll(existing =>
            string.Equals(existing.RunId, result.RunId, StringComparison.OrdinalIgnoreCase));
        configuration.ScheduleHistory.Insert(0, result.Clone());
        TrimScheduleHistory();
        configuration.Save();
    }

    private DadRunResult? ResolveScheduleDadRunResult(string requestId, DadRunResult visibleRun)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return null;

        if (!string.IsNullOrWhiteSpace(visibleRun.RequestId) &&
            string.Equals(visibleRun.RequestId, requestId, StringComparison.OrdinalIgnoreCase) &&
            visibleRun.Status != DadRunStatus.Idle)
        {
            return visibleRun.Clone();
        }

        return configuration.RunHistory?
            .FirstOrDefault(result => string.Equals(result.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
            ?.Clone();
    }

    private DadScheduledCrewJobResult? FindSchedulerResult(string jobId)
        => configuration.SchedulerHistory.FirstOrDefault(result =>
            string.Equals(result.JobId, jobId, StringComparison.OrdinalIgnoreCase));

    private string ValidateWholeSchedule(DadScheduleDefinition schedule)
    {
        var groupIds = BuildPlannerGroupIdSet();
        for (var index = 0; index < schedule.Entries.Count; index++)
        {
            var entry = schedule.Entries[index].Normalize();
            if (string.IsNullOrWhiteSpace(entry.GroupId))
                return $"Schedule entry {index + 1} has no saved preset.";
            if (!groupIds.Contains(entry.GroupId))
                return $"Schedule entry {index + 1} references missing preset '{entry.GroupId}'.";
        }

        return string.Empty;
    }

    private HashSet<string> BuildPlannerGroupIdSet()
    {
        configuration.PlannerGroups ??= [];
        return configuration.PlannerGroups
            .Where(static group => !string.IsNullOrWhiteSpace(group.GroupId))
            .Select(static group => group.GroupId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private DadPlannerGroup? FindPlannerGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        configuration.PlannerGroups ??= [];
        return configuration.PlannerGroups.FirstOrDefault(group =>
            string.Equals(group.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
    }

    private void RecordTerminalResult(DadScheduledCrewJob job, DadSchedulerPresetPhase phase, string summary)
    {
        if (!IsTerminalPhase(phase))
            return;

        RecordTerminalResult(new DadScheduledCrewJobResult
        {
            JobId = job.JobId,
            JobType = job.JobType,
            GroupId = job.GroupId,
            PresetName = job.PresetName,
            RequestedBy = job.RequestedBy,
            StartedAtUtc = job.CreatedAtUtc,
            CompletedAtUtc = DateTime.UtcNow,
            FinalPhase = phase,
            Success = IsSuccessfulTerminalPhase(phase),
            Summary = string.IsNullOrWhiteSpace(summary) ? phase.ToString() : summary,
            BlockedReason = phase == DadSchedulerPresetPhase.Cancelled ? summary : job.BlockedReason,
            ScheduleId = job.ScheduleId,
            ScheduleRunId = job.ScheduleRunId,
            ScheduleEntryId = job.ScheduleEntryId,
            ScheduleEntryIndex = job.ScheduleEntryIndex,
            ScheduleRepeatIteration = job.ScheduleRepeatIteration,
            ScheduleCadence = job.ScheduleCadence,
            ParentOperationJobId = job.ParentOperationJobId,
            LevelingIteration = job.LevelingIteration,
        });
    }

    private void RecordTerminalResult(DadSchedulerPresetState state)
    {
        if (!IsTerminalPhase(state.Phase))
            return;

        LogSchedulerPhaseTransition();

        RecordTerminalResult(new DadScheduledCrewJobResult
        {
            JobId = state.JobId,
            JobType = state.JobType,
            GroupId = state.GroupId,
            PresetName = state.PresetName,
            RequestedBy = state.RequestedBy,
            StartedAtUtc = state.StartedAtUtc,
            CompletedAtUtc = state.CompletedAtUtc ?? DateTime.UtcNow,
            FinalPhase = state.Phase,
            Success = IsSuccessfulTerminalPhase(state.Phase),
            Summary = state.Summary,
            BlockedReason = state.BlockedReason,
            ScheduleId = state.ScheduleId,
            ScheduleRunId = state.ScheduleRunId,
            ScheduleEntryId = state.ScheduleEntryId,
            ScheduleEntryIndex = state.ScheduleEntryIndex,
            ScheduleRepeatIteration = state.ScheduleRepeatIteration,
            ScheduleCadence = state.ScheduleCadence,
            SkipKind = state.SkipKind,
            ParentOperationJobId = state.ParentOperationJobId,
            LevelingIteration = state.LevelingIteration,
        });
    }

    private void RecordTerminalResult(DadScheduledCrewJobResult result)
    {
        if (crewFormation != null &&
            !string.IsNullOrWhiteSpace(crewFormation.SchedulerJobId) &&
            string.Equals(
                result.JobId,
                crewFormation.SchedulerJobId,
                StringComparison.Ordinal))
        {
            return;
        }

        NormalizeHistory();
        if (string.IsNullOrWhiteSpace(result.JobId))
            result.JobId = Guid.NewGuid().ToString("N");

        configuration.SchedulerHistory.RemoveAll(existing =>
            string.Equals(existing.JobId, result.JobId, StringComparison.OrdinalIgnoreCase));
        configuration.SchedulerHistory.Insert(0, result.Clone());
        TrimSchedulerHistory();
        configuration.Save();
    }

    private void RecoverAbandonedScheduleRun()
    {
        NormalizeSchedules();
        NormalizeScheduleHistory();
        NormalizeQueue();
        var active = configuration.ActiveScheduleRun ?? new DadScheduleRunState();
        if (!active.IsActive)
            return;

        // Scheduler runtime state is intentionally not reconstructed after reload. Remove only
        // jobs owned by the abandoned schedule run so a stale entry cannot start independently.
        configuration.SchedulerQueue.RemoveAll(job =>
            string.Equals(job.ScheduleRunId, active.RunId, StringComparison.OrdinalIgnoreCase));
        configuration.ActiveScheduleRun = DadScheduleRules.BlockRun(
            active,
            "Schedule run abandoned by coordinator/plugin reload; it can resume only from the persisted cursor through an explicit operator action and never replays automatically.",
            DateTime.UtcNow,
            DadScheduleFailureKind.CoordinatorReloadAbandonment);
        FinalizeScheduleRun(configuration.ActiveScheduleRun);
    }

    private DadScheduleDefinition? FindSchedule(string scheduleId)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            return null;

        configuration.Schedules ??= [];
        return configuration.Schedules.FirstOrDefault(schedule =>
            string.Equals(schedule.ScheduleId, scheduleId, StringComparison.OrdinalIgnoreCase));
    }

    private void NormalizeSchedules()
    {
        configuration.Schedules = DadScheduleRules.NormalizeSchedules(configuration.Schedules);
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        configuration.ActiveScheduleRun.RunId = configuration.ActiveScheduleRun.RunId?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.ScheduleId = configuration.ActiveScheduleRun.ScheduleId?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.ScheduleName = configuration.ActiveScheduleRun.ScheduleName?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.RequestedBy = string.IsNullOrWhiteSpace(configuration.ActiveScheduleRun.RequestedBy)
            ? string.Empty
            : configuration.ActiveScheduleRun.RequestedBy.Trim();
        configuration.ActiveScheduleRun.CurrentEntryId = configuration.ActiveScheduleRun.CurrentEntryId?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.CurrentGroupId = configuration.ActiveScheduleRun.CurrentGroupId?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.CurrentPresetName = configuration.ActiveScheduleRun.CurrentPresetName?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.ActiveSchedulerJobId = configuration.ActiveScheduleRun.ActiveSchedulerJobId?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.ActivePlannerRequestId = configuration.ActiveScheduleRun.ActivePlannerRequestId?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.Summary = configuration.ActiveScheduleRun.Summary?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.BlockedReason = configuration.ActiveScheduleRun.BlockedReason?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.RetriedFromRunId = configuration.ActiveScheduleRun.RetriedFromRunId?.Trim() ?? string.Empty;
        configuration.ActiveScheduleRun.ScheduleRevisionAtStart = Math.Max(0, configuration.ActiveScheduleRun.ScheduleRevisionAtStart);
        configuration.ActiveScheduleRun.RepeatIteration = Math.Max(1, configuration.ActiveScheduleRun.RepeatIteration);
        configuration.ActiveScheduleRun.TotalEntryExecutions = Math.Max(0, configuration.ActiveScheduleRun.TotalEntryExecutions);
        configuration.ActiveScheduleRun.CompletedEntryExecutions = Math.Clamp(
            configuration.ActiveScheduleRun.CompletedEntryExecutions,
            0,
            Math.Max(configuration.ActiveScheduleRun.TotalEntryExecutions, configuration.ActiveScheduleRun.CompletedEntryExecutions));
        configuration.ActiveScheduleRun.SkippedEntryExecutions = Math.Clamp(
            configuration.ActiveScheduleRun.SkippedEntryExecutions,
            0,
            configuration.ActiveScheduleRun.CompletedEntryExecutions);
    }

    private void NormalizeScheduleHistory()
    {
        configuration.ScheduleHistory ??= [];
        foreach (var result in configuration.ScheduleHistory)
        {
            result.RunId = result.RunId?.Trim() ?? string.Empty;
            result.ScheduleId = result.ScheduleId?.Trim() ?? string.Empty;
            result.ScheduleName = result.ScheduleName?.Trim() ?? string.Empty;
            result.Summary = result.Summary?.Trim() ?? string.Empty;
            result.BlockedReason = result.BlockedReason?.Trim() ?? string.Empty;
            result.RetriedFromRunId = result.RetriedFromRunId?.Trim() ?? string.Empty;
            result.CurrentEntryId = result.CurrentEntryId?.Trim() ?? string.Empty;
            result.CurrentGroupId = result.CurrentGroupId?.Trim() ?? string.Empty;
            result.CurrentPresetName = result.CurrentPresetName?.Trim() ?? string.Empty;
            result.ScheduleRevisionAtStart = Math.Max(0, result.ScheduleRevisionAtStart);
            result.CurrentEntryIndex = Math.Max(0, result.CurrentEntryIndex);
            result.RepeatIteration = Math.Max(1, result.RepeatIteration);
            result.CompletedEntryExecutions = Math.Max(0, result.CompletedEntryExecutions);
            result.TotalEntryExecutions = Math.Max(result.CompletedEntryExecutions, result.TotalEntryExecutions);
            result.SkippedEntryExecutions = Math.Clamp(result.SkippedEntryExecutions, 0, result.CompletedEntryExecutions);
        }

        TrimScheduleHistory();
    }

    private void TrimScheduleHistory()
    {
        if (configuration.ScheduleHistory.Count <= MaxScheduleHistory)
            return;

        configuration.ScheduleHistory = configuration.ScheduleHistory
            .OrderByDescending(static result => result.CompletedAtUtc)
            .ThenByDescending(static result => result.StartedAtUtc)
            .Take(MaxScheduleHistory)
            .ToList();
    }

    private void NormalizeHistory()
    {
        configuration.SchedulerHistory ??= [];
        foreach (var result in configuration.SchedulerHistory)
        {
            result.JobId = result.JobId?.Trim() ?? string.Empty;
            result.GroupId = result.GroupId?.Trim() ?? string.Empty;
            result.PresetName = result.PresetName?.Trim() ?? string.Empty;
            result.RequestedBy = string.IsNullOrWhiteSpace(result.RequestedBy) ? "scheduler" : result.RequestedBy.Trim();
            result.Summary = result.Summary?.Trim() ?? string.Empty;
            result.BlockedReason = result.BlockedReason?.Trim() ?? string.Empty;
            result.ScheduleId = result.ScheduleId?.Trim() ?? string.Empty;
            result.ScheduleRunId = result.ScheduleRunId?.Trim() ?? string.Empty;
            result.ScheduleEntryId = result.ScheduleEntryId?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(result.ScheduleCadence))
                result.ScheduleCadence = DadScheduleCadence.Manual;
            if (!Enum.IsDefined(result.SkipKind))
                result.SkipKind = DadSchedulerSkipKind.None;
        }

        TrimSchedulerHistory();
    }

    private void TrimSchedulerHistory()
    {
        if (configuration.SchedulerHistory.Count <= MaxSchedulerHistory)
            return;

        configuration.SchedulerHistory = configuration.SchedulerHistory
            .OrderByDescending(static result => result.CompletedAtUtc)
            .ThenByDescending(static result => result.StartedAtUtc)
            .Take(MaxSchedulerHistory)
            .ToList();
    }

    private void NormalizeLaunchProfiles()
    {
        configuration.LaunchProfiles ??= [];
        foreach (var profile in configuration.LaunchProfiles)
            profile.Normalize();
    }

    private IReadOnlyList<DadLaunchProfile> BuildNormalizedLaunchProfileSnapshot()
        => DadSchedulerGroupCloneRules.CloneNormalizedLaunchProfiles(configuration.LaunchProfiles);

    private void NormalizeQueue()
    {
        configuration.SchedulerQueue ??= [];
        configuration.SchedulerHistory ??= [];
        foreach (var job in configuration.SchedulerQueue)
        {
            job.JobId = string.IsNullOrWhiteSpace(job.JobId) ? Guid.NewGuid().ToString("N") : job.JobId.Trim();
            job.PresetName = job.PresetName?.Trim() ?? string.Empty;
            job.GroupId = job.GroupId?.Trim() ?? string.Empty;
            job.RequestedBy = string.IsNullOrWhiteSpace(job.RequestedBy) ? "scheduler" : job.RequestedBy.Trim();
            job.MapRunTemplate = job.MapRunTemplate?.Trim() ?? string.Empty;
            job.ScheduleId = job.ScheduleId?.Trim() ?? string.Empty;
            job.ScheduleRunId = job.ScheduleRunId?.Trim() ?? string.Empty;
            job.ScheduleEntryId = job.ScheduleEntryId?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(job.ScheduleCadence))
                job.ScheduleCadence = DadScheduleCadence.Manual;
            job.TargetCharacters ??= [];
            job.TargetCharacterKeys ??= [];
            job.TargetAccountKeys ??= [];
            job.TargetCharacterKeys = job.TargetCharacterKeys
                .Where(static key => !key.IsEmpty)
                .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            job.TargetAccountKeys = job.TargetAccountKeys
                .Where(static key => !key.IsEmpty)
                .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            job.TargetCharacters = job.TargetCharacters
                .Where(static target => target is { IsEmpty: false })
                .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}

internal readonly record struct DadSchedulerUiRevision(int SchedulerToken, int LaunchProfilesToken);
