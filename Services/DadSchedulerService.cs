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

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadProfileDirectoryService profileDirectoryService;
    private readonly DadCharacterIntelligenceService characterIntelligenceService;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadRosterCatalogService rosterCatalogService;
    private readonly IPluginLog log;
    private DadSchedulerPresetState currentState = new() { Phase = DadSchedulerPresetPhase.Idle, Summary = "Scheduler idle." };
    private DadScheduledCrewJob? activeJob;
    private DateTime nextRefreshUtc = DateTime.MinValue;

    public DadSchedulerService(
        Configuration configuration,
        ConfigManager configManager,
        DadProfileDirectoryService profileDirectoryService,
        DadCharacterIntelligenceService characterIntelligenceService,
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadRosterCatalogService rosterCatalogService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.profileDirectoryService = profileDirectoryService;
        this.characterIntelligenceService = characterIntelligenceService;
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.rosterCatalogService = rosterCatalogService;
        this.log = log;
    }

    public DadSchedulerPresetState CurrentState => currentState.Clone();

    internal DadSchedulerUiRevision GetPlannerUiRevision()
    {
        var schedulerHash = new HashCode();
        schedulerHash.Add(currentState.SchedulerRunId, StringComparer.Ordinal);
        schedulerHash.Add(currentState.JobId, StringComparer.Ordinal);
        schedulerHash.Add(currentState.Phase);
        schedulerHash.Add(currentState.UpdatedAtUtc.Ticks);
        schedulerHash.Add(currentState.CompletedAtUtc?.Ticks ?? 0);
        schedulerHash.Add(currentState.PlannerStarted);
        schedulerHash.Add(currentState.Slots.Count);
        foreach (var slot in currentState.Slots)
        {
            schedulerHash.Add(slot.SlotId, StringComparer.Ordinal);
            schedulerHash.Add(slot.WakePolicy);
            schedulerHash.Add(slot.LaunchStarted);
            schedulerHash.Add(slot.LoadCommandSentUtc?.Ticks ?? 0);
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
        var activeJobClone = currentState.IsActive ? activeJob?.Clone() : null;
        return new DadSchedulerQueueSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            ActiveJob = activeJobClone,
            ActiveState = currentState.Clone(),
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
            Summary = currentState.IsActive
                ? $"Active {currentState.JobType}: {currentState.Summary}"
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

    public DadScheduleRunState StartScheduleRun(string scheduleId, bool dryRun, string requestedBy)
    {
        NormalizeSchedules();
        NormalizeScheduleHistory();
        NormalizeQueue();
        NormalizeHistory();
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
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
            return configuration.ActiveScheduleRun.Clone();

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
    {
        NormalizeSchedules();
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        if (!configuration.ActiveScheduleRun.IsActive)
            return false;

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

    public DadScheduledCrewJob EnqueueScheduledPreset(DadPlannerGroup group, DadScheduledPresetRequest request)
    {
        NormalizeQueue();
        var existing = FindEquivalentActiveOrPendingJob(request.JobType, group.GroupId);
        if (existing != null)
            return existing.Clone();

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
            TargetCharacterKeys = request.TargetCharacterKeys == null ? [] : [..request.TargetCharacterKeys],
        };
        job.StatusSummary = BuildQueuedJobSummary(job);

        configuration.SchedulerQueue.Add(job);
        configuration.Save();
        return job.Clone();
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

        if (activeJob != null &&
            string.Equals(activeJob.JobId, jobId, StringComparison.OrdinalIgnoreCase) &&
            currentState.IsActive)
        {
            Cancel(string.IsNullOrWhiteSpace(reason) ? $"Scheduler job {jobId} cancelled." : reason);
            return true;
        }

        return false;
    }

    public int ClearAccountData()
    {
        NormalizeQueue();
        var clearedJobs = configuration.SchedulerQueue.Count;
        configuration.SchedulerQueue.Clear();
        activeJob = null;
        currentState = new DadSchedulerPresetState
        {
            Phase = DadSchedulerPresetPhase.Idle,
            Summary = "Scheduler account data cleared.",
        };
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
        DadPlannerRunRequestPreview plannerRequestPreview,
        bool forcePeerRefresh = false)
    {
        NormalizeLaunchProfiles();
        configuration.CharacterLoadInstruction ??= new DadCharacterLoadInstruction();
        configuration.CharacterLoadInstruction.Normalize();
        var pool = forcePeerRefresh
            ? characterIntelligenceService.RequestPeerSnapshots()
            : characterIntelligenceService.CurrentPool;
        var preview = new DadSchedulerPreview
        {
            GeneratedAtUtc = DateTime.UtcNow,
            GroupId = group?.GroupId ?? string.Empty,
            PresetName = group?.DisplayName ?? "Auto roster",
            Phase = currentState.IsActive ? currentState.Phase : DadSchedulerPresetPhase.Resolving,
            PlannerRequestPreview = plannerRequestPreview,
            LaunchProfiles = configuration.LaunchProfiles.Select(static profile => profile.Clone()).ToList(),
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

        var effectiveGroup = BuildEffectiveSchedulerGroup(group, pool, plannerRequestPreview);
        if (!plannerRequestPreview.CanStart || plannerRequestPreview.Request == null)
        {
            BlockPreview(preview, string.IsNullOrWhiteSpace(plannerRequestPreview.BlockedReason)
                ? plannerRequestPreview.StatusSummary
                : plannerRequestPreview.BlockedReason);
            preview.Slots = BuildSlotStates(effectiveGroup, pool, currentState.Slots);
            return preview;
        }

        preview.Slots = BuildSlotStates(effectiveGroup, pool, currentState.Slots);
        var blockers = preview.Slots
            .Select(static slot => slot.BlockedReason)
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockers.Count > 0)
        {
            BlockPreview(preview, string.Join(" | ", blockers));
            return preview;
        }

        preview.ReadyToStart = preview.Slots.All(static slot => slot.Ready);
        preview.CanStart = true;
        preview.StatusSummary = preview.ReadyToStart
            ? $"Scheduler ready: all {preview.Slots.Count} preset slot(s) are online."
            : $"Scheduler can start: {preview.Slots.Count(static slot => slot.Ready)}/{preview.Slots.Count} preset slot(s) ready.";
        return preview;
    }

    public DadSchedulerPresetState StartPreset(
        DadPlannerGroup group,
        DadPlannerRunRequestPreview plannerRequestPreview,
        bool dryRun,
        DadScheduledCrewJob? job = null)
    {
        var preview = BuildPreview(group, plannerRequestPreview, forcePeerRefresh: true);
        activeJob = job?.Clone() ?? new DadScheduledCrewJob
        {
            JobType = DadSchedulerJobType.ScheduledPreset,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            DryRun = dryRun,
            RequestedBy = "scheduler",
        };
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
        };

        if (!preview.CanStart)
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = $"Scheduler blocked for preset '{group.DisplayName}'.";
            currentState.BlockedReason = preview.BlockedReason;
            currentState.CompletedAtUtc = DateTime.UtcNow;
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

        currentState.Phase = preview.ReadyToStart
            ? DadSchedulerPresetPhase.ReadyToStart
            : DadSchedulerPresetPhase.Resolving;
        currentState.Summary = preview.StatusSummary;
        nextRefreshUtc = DateTime.MinValue;
        return CurrentState;
    }

    public void Update(
        Func<string, DadPlannerGroup?> groupResolver,
        Func<string, DadPlannerRunRequestPreview?> plannerPreviewBuilder,
        Func<DadRunRequest, DadRunResult> startPlannerRequest,
        Func<DadRunResult>? visibleRunProvider = null)
    {
        TickScheduleEnqueue();

        UpdateActiveScheduleRun(visibleRunProvider?.Invoke() ?? DadRunResult.Idle());

        if (!currentState.IsActive)
        {
            TryStartNextQueuedJob(groupResolver, plannerPreviewBuilder);
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

        var plannerPreview = plannerPreviewBuilder(currentState.GroupId);
        if (plannerPreview?.Request == null)
        {
            BlockActive("Scheduler could not rebuild the preset planner request.");
            return;
        }

        var groupPreviewSlots = currentState.Slots;
        var previewSlots = RebuildActiveSlots(plannerPreview, groupPreviewSlots);
        currentState.Slots = previewSlots;
        currentState.UpdatedAtUtc = DateTime.UtcNow;

        foreach (var slot in currentState.Slots)
        {
            if (slot.Ready)
                continue;

            if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
            {
                BlockActive(slot.BlockedReason);
                return;
            }

            if (!slot.IsOnline && slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline)
            {
                if (!TryStartLaunch(slot, out var launchBlocker))
                {
                    BlockActive(launchBlocker);
                    return;
                }
            }

            if (slot.IsOnline &&
                !slot.CorrectCharacter &&
                slot.WakePolicy is DadSchedulerWakePolicy.LoadCharacterIfOnline or DadSchedulerWakePolicy.LaunchIfOffline &&
                !slot.LoadCommandSentUtc.HasValue)
            {
                if (!TrySendCharacterLoadCommand(slot, out var loadBlocker))
                {
                    if (!string.IsNullOrWhiteSpace(loadBlocker))
                        BlockActive(loadBlocker);
                    return;
                }
            }

            if (IsLaunchTimedOut(slot, out var timeoutReason) || IsLoadTimedOut(slot, out timeoutReason))
            {
                currentState.Phase = DadSchedulerPresetPhase.TimedOut;
                currentState.BlockedReason = timeoutReason;
                currentState.Summary = timeoutReason;
                currentState.CompletedAtUtc = DateTime.UtcNow;
                currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
                RecordTerminalResult(currentState);
                return;
            }
        }

        if (!currentState.Slots.All(static slot => slot.Ready))
        {
            currentState.Phase = ResolveWaitingPhase(currentState.Slots);
            currentState.Summary = $"Scheduler waiting: {currentState.Slots.Count(static slot => slot.Ready)}/{currentState.Slots.Count} slot(s) ready.";
            return;
        }

        currentState.Phase = DadSchedulerPresetPhase.StartingPlanner;
        currentState.Summary = $"Scheduler ready; starting preset '{currentState.PresetName}'.";
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        var result = startPlannerRequest(plannerPreview.Request);
        currentState.PlannerStarted = result.Status != DadRunStatus.Rejected;
        currentState.Phase = currentState.PlannerStarted
            ? DadSchedulerPresetPhase.StartedPlanner
            : DadSchedulerPresetPhase.Blocked;
        currentState.Summary = currentState.PlannerStarted
            ? $"Scheduler started preset '{currentState.PresetName}': {result.Summary}"
            : $"Scheduler could not start preset '{currentState.PresetName}'.";
        currentState.BlockedReason = currentState.PlannerStarted ? string.Empty : result.Summary;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
        RecordTerminalResult(currentState);
    }

    public void TickScheduleEnqueue()
    {
        NormalizeQueue();
        configuration.PlannerGroups ??= [];
        var now = DateTime.UtcNow;
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
            if (FindEquivalentActiveOrPendingJob(jobType, group.GroupId) == null)
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
        if (!currentState.IsActive)
            return;

        currentState.Phase = DadSchedulerPresetPhase.Cancelled;
        currentState.Summary = string.IsNullOrWhiteSpace(reason) ? "Scheduler cancelled." : reason;
        currentState.BlockedReason = currentState.Summary;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        RecordTerminalResult(currentState);
    }

    private bool TryStartNextQueuedJob(
        Func<string, DadPlannerGroup?> groupResolver,
        Func<string, DadPlannerRunRequestPreview?> plannerPreviewBuilder)
    {
        NormalizeQueue();
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

        var pool = characterIntelligenceService.RequestPeerSnapshots();
        currentState.Slots = BuildSlotStates(group, pool, []);
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
                RequiredAccountKey = slot.RequiredAccountKey,
                RequiredCharacterKey = slot.RequiredCharacterKey,
                WakePolicy = slot.WakePolicy,
                LaunchProfileId = slot.LaunchProfileId,
            }).ToList(),
        };
        currentState.Slots = BuildSlotStates(group, pool, currentState.Slots, allowRosterMaintenanceTarget: true);
        currentState.UpdatedAtUtc = DateTime.UtcNow;

        foreach (var slot in currentState.Slots)
        {
            if (slot.Ready)
                continue;

            if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
            {
                BlockActive(slot.BlockedReason);
                return;
            }

            if (!slot.IsOnline && slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline)
            {
                if (!TryStartLaunch(slot, out var launchBlocker))
                {
                    BlockActive(launchBlocker);
                    return;
                }
            }

            if (slot.IsOnline &&
                !slot.CorrectCharacter &&
                !slot.LoadCommandSentUtc.HasValue)
            {
                if (!TrySendCharacterLoadCommand(slot, out var loadBlocker))
                {
                    if (!string.IsNullOrWhiteSpace(loadBlocker))
                        BlockActive(loadBlocker);
                    return;
                }
            }

            if (IsLaunchTimedOut(slot, out var timeoutReason) || IsLoadTimedOut(slot, out timeoutReason))
            {
                currentState.Phase = DadSchedulerPresetPhase.TimedOut;
                currentState.BlockedReason = timeoutReason;
                currentState.Summary = timeoutReason;
                currentState.CompletedAtUtc = DateTime.UtcNow;
                currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
                RecordTerminalResult(currentState);
                return;
            }
        }

        if (!currentState.Slots.All(static slot => slot.Ready))
        {
            currentState.Phase = ResolveWaitingPhase(currentState.Slots);
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

        foreach (var slot in currentState.Slots)
        {
            if (slot.Ready)
                continue;

            if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
            {
                BlockActive(slot.BlockedReason);
                return;
            }

            if (!slot.IsOnline && slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline)
            {
                if (!TryStartLaunch(slot, out var launchBlocker))
                {
                    BlockActive(launchBlocker);
                    return;
                }
            }

            if (slot.IsOnline &&
                !slot.CorrectCharacter &&
                slot.WakePolicy is DadSchedulerWakePolicy.LoadCharacterIfOnline or DadSchedulerWakePolicy.LaunchIfOffline &&
                !slot.LoadCommandSentUtc.HasValue)
            {
                if (!TrySendCharacterLoadCommand(slot, out var loadBlocker))
                {
                    if (!string.IsNullOrWhiteSpace(loadBlocker))
                        BlockActive(loadBlocker);
                    return;
                }
            }

            if (IsLaunchTimedOut(slot, out var timeoutReason) || IsLoadTimedOut(slot, out timeoutReason))
            {
                currentState.Phase = DadSchedulerPresetPhase.TimedOut;
                currentState.BlockedReason = timeoutReason;
                currentState.Summary = timeoutReason;
                currentState.CompletedAtUtc = DateTime.UtcNow;
                currentState.UpdatedAtUtc = currentState.CompletedAtUtc.Value;
                RecordTerminalResult(currentState);
                return;
            }
        }

        if (!currentState.Slots.All(static slot => slot.Ready))
        {
            currentState.Phase = ResolveWaitingPhase(currentState.Slots);
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
        DadPlannerRunRequestPreview plannerPreview,
        IReadOnlyList<DadSchedulerSlotState> previousSlots)
    {
        var pool = characterIntelligenceService.RequestPeerSnapshots();
        var syntheticGroup = new DadPlannerGroup
        {
            GroupId = currentState.GroupId,
            DisplayName = currentState.PresetName,
            Slots = previousSlots.Select(static slot => new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                RequiredAccountKey = slot.RequiredAccountKey,
                RequiredCharacterKey = slot.RequiredCharacterKey,
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

        currentState.PlannerRequestId = plannerPreview.Request?.RequestId ?? currentState.PlannerRequestId;
        return rebuilt;
    }

    private DadPlannerGroup BuildEffectiveSchedulerGroup(
        DadPlannerGroup group,
        DadCharacterPool pool,
        DadPlannerRunRequestPreview plannerRequestPreview)
    {
        if (!IsLocalNpcPlannerRequest(plannerRequestPreview) || group.Slots.Count <= 1)
            return group;

        var participants = BuildParticipantSet(pool);
        var effectiveSlot = SelectEffectiveLocalNpcSchedulerSlot(group.Slots, participants);
        return CloneSchedulerGroupWithSlots(group, [effectiveSlot]);
    }

    private static bool IsLocalNpcPlannerRequest(DadPlannerRunRequestPreview plannerRequestPreview)
        => plannerRequestPreview.Request?.DutySupport != null
           || plannerRequestPreview.Request?.Trust != null
           || plannerRequestPreview.Request?.Orchestration.ModuleTarget is DadModuleId.DutySupport or DadModuleId.Trust
           || plannerRequestPreview.PlannerPreview.ActivityMode is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust;

    private static DadPlannerGroupSlot SelectEffectiveLocalNpcSchedulerSlot(
        IReadOnlyList<DadPlannerGroupSlot> slots,
        IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var matchingSlot = slots.FirstOrDefault(slot =>
            participants.Any(participant => participant.IsLocalClient &&
                                            MatchesSlotAccount(participant, slot) &&
                                            (slot.RequiredCharacterKey.IsEmpty ||
                                             MatchesSlotCharacter(participant, slot))));

        var effectiveSlot = CloneSchedulerGroupSlot(matchingSlot ?? slots[0]);
        effectiveSlot.SlotId = DadPlannerSlotRules.LeaderSlotId;
        effectiveSlot.IsSubstitute = false;
        return effectiveSlot;
    }

    private static DadPlannerGroup CloneSchedulerGroupWithSlots(
        DadPlannerGroup source,
        IEnumerable<DadPlannerGroupSlot> slots)
        => new()
        {
            GroupId = source.GroupId,
            DisplayName = source.DisplayName,
            RunFamily = source.RunFamily,
            ActivityMode = source.ActivityMode,
            OperatorMode = source.OperatorMode,
            ConnectedOnly = source.ConnectedOnly,
            SameDatacenterOnly = source.SameDatacenterOnly,
            AllowStaleForPlanning = source.AllowStaleForPlanning,
            TransportOwner = source.TransportOwner,
            QueueAuthority = source.QueueAuthority,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = source.DutyDisplayName,
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            MogtomePreset = source.MogtomePreset,
            MogtomeDutyPolicy = source.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = source.RefreshTrustNpcLevels,
            StopPolicy = source.StopPolicy.Clone(),
            CompletionActions = source.CompletionActions?.Clone(),
            Slots = slots.Select(CloneSchedulerGroupSlot).ToList(),
            ScheduleEnabled = source.ScheduleEnabled,
            ScheduleCadenceHours = source.ScheduleCadenceHours,
            NextEligibleTimeUtc = source.NextEligibleTimeUtc,
            ScheduleRequester = source.ScheduleRequester,
            SchedulePriority = source.SchedulePriority,
            MapRunTemplate = source.MapRunTemplate,
            MapMode = source.MapMode,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
        };

    private static DadPlannerGroupSlot CloneSchedulerGroupSlot(DadPlannerGroupSlot source)
        => new()
        {
            SlotId = source.SlotId,
            IsSubstitute = source.IsSubstitute,
            RequiredRole = source.RequiredRole,
            RequiredAccountKey = source.RequiredAccountKey,
            RequiredCharacterKey = source.RequiredCharacterKey,
            WakePolicy = source.WakePolicy,
            LaunchProfileId = source.LaunchProfileId,
            CharacterLoadInstruction = source.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction(),
            AllowSubstitution = source.AllowSubstitution,
        };

    private List<DadSchedulerSlotState> BuildSlotStates(
        DadPlannerGroup group,
        DadCharacterPool pool,
        IReadOnlyList<DadSchedulerSlotState> previousSlots,
        bool allowRosterMaintenanceTarget = false)
    {
        var participants = BuildParticipantSet(pool);
        return group.Slots.Select(slot =>
        {
            var previous = previousSlots.FirstOrDefault(existing =>
                string.Equals(existing.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
            var state = BuildSlotState(slot, participants, allowRosterMaintenanceTarget);
            if (previous != null)
            {
                state.LaunchStarted = previous.LaunchStarted;
                state.LaunchStartedUtc = previous.LaunchStartedUtc;
                state.LoadCommandSentUtc = previous.LoadCommandSentUtc;
            }

            return state;
        }).ToList();
    }

    private DadSchedulerSlotState BuildSlotState(
        DadPlannerGroupSlot slot,
        IReadOnlyList<DadParticipantSnapshot> participants,
        bool allowRosterMaintenanceTarget = false)
    {
        var state = new DadSchedulerSlotState
        {
            SlotId = string.IsNullOrWhiteSpace(slot.SlotId) ? "Slot" : slot.SlotId,
            WakePolicy = slot.WakePolicy,
            RequiredAccountKey = slot.RequiredAccountKey,
            RequiredCharacterKey = slot.RequiredCharacterKey,
            LaunchProfileId = slot.LaunchProfileId?.Trim() ?? string.Empty,
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

        var matchingAccount = participants.FirstOrDefault(participant => MatchesSlotAccount(participant, slot));
        var matchingCharacter = participants.FirstOrDefault(participant =>
            MatchesSlotCharacter(participant, slot) &&
            (slot.RequiredAccountKey.IsEmpty || MatchesSlotAccount(participant, slot)));
        var selected = matchingCharacter ?? matchingAccount;
        if (selected != null)
        {
            state.IsOnline = true;
            state.CorrectCharacter = matchingCharacter != null || slot.RequiredCharacterKey.IsEmpty;
            state.MatchedWorkerSessionId = selected.WorkerSessionId;
            state.ActiveCharacterKey = selected.ActiveCharacterKey;
            state.Ready = state.CorrectCharacter;
            state.Summary = state.Ready
                ? $"Online on {selected.ActiveCharacterKey}."
                : $"Account online as {selected.ActiveCharacterKey}; needs {slot.RequiredCharacterKey}.";

            if (!state.Ready &&
                slot.WakePolicy is not DadSchedulerWakePolicy.LoadCharacterIfOnline and not DadSchedulerWakePolicy.LaunchIfOffline)
                state.BlockedReason = $"Slot {state.SlotId} is online on the wrong character and wake policy is {slot.WakePolicy}.";
        }
        else
        {
            state.Summary = $"Slot {state.SlotId} is offline.";
        }

        ApplyLaunchProfileState(state, slot);
        ApplyWakePolicyBlockers(state, slot);
        return state;
    }

    private void ApplyLaunchProfileState(DadSchedulerSlotState state, DadPlannerGroupSlot slot)
    {
        var profile = ResolveLaunchProfile(slot);
        if (profile == null)
            return;

        profile.Normalize();
        state.LaunchProfileId = profile.ProfileId;
        state.LaunchProfileName = profile.DisplayName;
        state.BatchPath = profile.BatchPath;
        state.LaunchProfileDryRun = profile.DryRun;
    }

    private void ApplyWakePolicyBlockers(DadSchedulerSlotState state, DadPlannerGroupSlot slot)
    {
        if (!string.IsNullOrWhiteSpace(state.BlockedReason))
            return;

        if (state.Ready)
            return;

        if (state.WakePolicy == DadSchedulerWakePolicy.AlreadyOnlineOnly)
        {
            state.BlockedReason = state.IsOnline
                ? state.BlockedReason
                : $"Slot {state.SlotId} requires an already-online Dad client.";
            return;
        }

        if (state.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline && !state.IsOnline)
        {
            var profile = ResolveLaunchProfile(slot);
            if (profile == null)
            {
                state.BlockedReason = $"Slot {state.SlotId} is offline and has no matching launch profile.";
                return;
            }

            profile.Normalize();
            state.LaunchProfileId = profile.ProfileId;
            state.LaunchProfileName = profile.DisplayName;
            state.BatchPath = profile.BatchPath;
            state.LaunchProfileDryRun = profile.DryRun;
            if (!slot.RequiredAccountKey.IsEmpty &&
                !profile.AccountKey.IsEmpty &&
                !DadRosterIdentity.SameAccount(profile.AccountKey, slot.RequiredAccountKey))
            {
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' belongs to account {profile.AccountKey}, not {slot.RequiredAccountKey}.";
            }
            else if (!profile.Enabled)
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' is disabled.";
            else if (!profile.AllowAutoStart)
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' does not allow auto-start.";
            else if (profile.DryRun)
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' is dry-run only; Dad will not start clients.";
            else if (string.IsNullOrWhiteSpace(profile.BatchPath))
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' has no batch path.";
            else if (!IsAllowedBootBatchPath(profile.BatchPath, ClientBootDirectory))
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' must be an imported .bat under {ClientBootDirectory}.";
            else if (!File.Exists(profile.BatchPath))
                state.BlockedReason = $"Launch profile batch path not found: {profile.BatchPath}.";
            else
                state.Summary = $"Launch profile ready: {profile.BatchPath} for account {profile.AccountKey}.";
            return;
        }

        if (state.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline && state.IsOnline && !state.CorrectCharacter)
        {
            var instruction = ResolveLoadInstruction(slot);
            var command = instruction.BuildCommand(slot.RequiredCharacterKey, slot.RequiredAccountKey);
            if (string.IsNullOrWhiteSpace(command))
            {
                state.BlockedReason = $"Slot {state.SlotId} is online on wrong character and needs character load command template before loading {slot.RequiredCharacterKey}.";
                return;
            }

            if (instruction.DryRun)
            {
                state.BlockedReason = $"Character-load command is dry-run only; would send: {command}";
                return;
            }

            state.Summary = $"Character-load command ready for {slot.RequiredCharacterKey}.";
            return;
        }

        if (state.WakePolicy == DadSchedulerWakePolicy.LoadCharacterIfOnline)
        {
            if (!state.IsOnline)
            {
                state.BlockedReason = $"Slot {state.SlotId} needs account already online before character load.";
                return;
            }

            var instruction = ResolveLoadInstruction(slot);
            var command = instruction.BuildCommand(slot.RequiredCharacterKey, slot.RequiredAccountKey);
            if (string.IsNullOrWhiteSpace(command))
            {
                state.BlockedReason = $"Slot {state.SlotId} needs character load command template before loading {slot.RequiredCharacterKey}.";
                return;
            }

            if (instruction.DryRun)
            {
                state.BlockedReason = $"Character-load command is dry-run only; would send: {command}";
                return;
            }

            state.Summary = $"Character-load command ready for {slot.RequiredCharacterKey}.";
        }
    }

    private IReadOnlyList<DadParticipantSnapshot> BuildParticipantSet(DadCharacterPool pool)
    {
        var participants = new List<DadParticipantSnapshot> { presenceService.BuildSnapshotCopy() };
        participants.AddRange(pool.PeerTransport.KnownParticipants.Select(static participant => participant.Clone()));
        return participants
            .Where(static participant => participant.IsAvailable && participant.State != DadParticipantState.Stale)
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

    private DadLaunchProfile? ResolveLaunchProfile(DadPlannerGroupSlot slot)
    {
        NormalizeLaunchProfiles();
        if (!string.IsNullOrWhiteSpace(slot.LaunchProfileId))
        {
            var exact = configuration.LaunchProfiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, slot.LaunchProfileId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
        }

        var primaryLaunchProfileId = ResolvePrimaryLaunchProfileId(slot.RequiredAccountKey);
        if (!string.IsNullOrWhiteSpace(primaryLaunchProfileId))
        {
            var primary = configuration.LaunchProfiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, primaryLaunchProfileId, StringComparison.OrdinalIgnoreCase) &&
                (profile.AccountKey.IsEmpty || DadRosterIdentity.SameAccount(profile.AccountKey, slot.RequiredAccountKey)));
            if (primary != null)
                return primary;
        }

        return configuration.LaunchProfiles.FirstOrDefault(profile =>
                   !slot.RequiredCharacterKey.IsEmpty &&
                   (slot.RequiredAccountKey.IsEmpty ||
                    profile.AccountKey.IsEmpty ||
                    DadRosterIdentity.SameAccount(profile.AccountKey, slot.RequiredAccountKey)) &&
                   profile.ExpectedCharacterKeys.Any(key =>
                       string.Equals(key.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase)));
    }

    private DadLaunchProfile? ResolveLaunchProfile(DadSchedulerSlotState slot)
    {
        NormalizeLaunchProfiles();
        if (!string.IsNullOrWhiteSpace(slot.LaunchProfileId))
        {
            var exact = configuration.LaunchProfiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, slot.LaunchProfileId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
        }

        var primaryLaunchProfileId = ResolvePrimaryLaunchProfileId(slot.RequiredAccountKey);
        if (!string.IsNullOrWhiteSpace(primaryLaunchProfileId))
        {
            var primary = configuration.LaunchProfiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, primaryLaunchProfileId, StringComparison.OrdinalIgnoreCase) &&
                (profile.AccountKey.IsEmpty || DadRosterIdentity.SameAccount(profile.AccountKey, slot.RequiredAccountKey)));
            if (primary != null)
                return primary;
        }

        return configuration.LaunchProfiles.FirstOrDefault(profile =>
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

    private DadCharacterLoadInstruction ResolveLoadInstruction(DadPlannerGroupSlot slot)
    {
        var slotInstruction = slot.CharacterLoadInstruction ?? new DadCharacterLoadInstruction();
        slotInstruction.Normalize();
        if (slotInstruction.Enabled)
            return slotInstruction;

        configuration.CharacterLoadInstruction ??= new DadCharacterLoadInstruction();
        return configuration.CharacterLoadInstruction.Normalize();
    }

    private DadCharacterLoadInstruction ResolveLoadInstruction(DadSchedulerSlotState slot)
    {
        configuration.CharacterLoadInstruction ??= new DadCharacterLoadInstruction();
        return configuration.CharacterLoadInstruction.Normalize();
    }

    private bool TryStartLaunch(DadSchedulerSlotState slot, out string blocker)
    {
        blocker = string.Empty;
        if (slot.LaunchStarted)
            return true;

        var profile = ResolveLaunchProfile(slot);
        if (profile == null)
        {
            blocker = $"Slot {slot.SlotId} is offline and has no launch profile.";
            return false;
        }

        profile.Normalize();
        if (!slot.RequiredAccountKey.IsEmpty &&
            !profile.AccountKey.IsEmpty &&
            !DadRosterIdentity.SameAccount(profile.AccountKey, slot.RequiredAccountKey))
        {
            blocker = $"Launch profile '{profile.DisplayName}' belongs to account {profile.AccountKey}, not {slot.RequiredAccountKey}.";
            return false;
        }
        if (!profile.Enabled)
        {
            blocker = $"Launch profile '{profile.DisplayName}' is disabled.";
            return false;
        }

        if (!profile.AllowAutoStart)
        {
            blocker = $"Launch profile '{profile.DisplayName}' does not allow auto-start.";
            return false;
        }

        if (profile.DryRun)
        {
            blocker = $"Launch profile '{profile.DisplayName}' is dry-run only; would start {profile.BatchPath}.";
            return false;
        }

        blocker = $"Launch profile '{profile.DisplayName}' cannot start {profile.BatchPath}; local OS process launching is disabled. Start the client manually.";
        return false;
    }

    private bool TrySendCharacterLoadCommand(DadSchedulerSlotState slot, out string blocker)
    {
        blocker = string.Empty;
        var instruction = ResolveLoadInstruction(slot);
        var command = instruction.BuildCommand(slot.RequiredCharacterKey, slot.RequiredAccountKey);
        if (string.IsNullOrWhiteSpace(command))
        {
            blocker = $"Slot {slot.SlotId} has no character-load command template.";
            return false;
        }

        if (instruction.DryRun)
        {
            blocker = $"Character-load command is dry-run only; would send: {command}";
            return false;
        }

        var commandDto = new DadCharacterLoadCommandDto
        {
            CommandId = $"{currentState.JobId}:{slot.SlotId}:character-load",
            AccountKey = slot.RequiredAccountKey,
            CharacterKey = slot.RequiredCharacterKey,
            Command = command,
            DryRun = false,
        };

        var participant = BuildParticipantSet(characterIntelligenceService.CurrentPool).FirstOrDefault(candidate =>
            string.Equals(candidate.WorkerSessionId.Value, slot.MatchedWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase));
        if (participant == null)
        {
            blocker = $"Slot {slot.SlotId} lost online participant before character-load command.";
            return false;
        }

        DadCharacterLoadResultDto? result;
        if (participant.IsLocalClient)
        {
            var accepted = Plugin.CommandManager.ProcessCommand(command);
            result = new DadCharacterLoadResultDto
            {
                CommandId = commandDto.CommandId,
                Accepted = accepted,
                DryRun = false,
                Summary = accepted ? $"Sent local character-load command: {command}" : $"Command manager rejected: {command}",
                Snapshot = presenceService.BuildSnapshotCopy(),
            };
        }
        else
        {
            result = transportService.SendCharacterLoadCommand(participant, commandDto);
        }

        if (result?.Accepted != true)
        {
            if (result == null)
            {
                slot.Summary = $"Character-load command queued for {slot.RequiredCharacterKey}; awaiting acknowledgement.";
                currentState.Phase = DadSchedulerPresetPhase.LoadingCharacters;
                return false;
            }

            blocker = result.Summary;
            return false;
        }

        slot.LoadCommandSentUtc = DateTime.UtcNow;
        slot.Summary = $"Sent character-load command for {slot.RequiredCharacterKey}; waiting for Dad heartbeat.";
        currentState.Phase = DadSchedulerPresetPhase.LoadingCharacters;
        return true;
    }

    private bool IsLaunchTimedOut(DadSchedulerSlotState slot, out string reason)
    {
        reason = string.Empty;
        if (!slot.LaunchStartedUtc.HasValue || slot.Ready)
            return false;

        var profile = ResolveLaunchProfile(slot);
        var timeout = TimeSpan.FromSeconds(Math.Max(30, profile?.TimeoutSeconds ?? 300));
        if (DateTime.UtcNow - slot.LaunchStartedUtc.Value < timeout)
            return false;

        reason = $"Launch timeout for slot {slot.SlotId}; no matching Dad heartbeat for account {slot.RequiredAccountKey}.";
        return true;
    }

    private bool IsLoadTimedOut(DadSchedulerSlotState slot, out string reason)
    {
        reason = string.Empty;
        if (!slot.LoadCommandSentUtc.HasValue || slot.Ready)
            return false;

        var instruction = ResolveLoadInstruction(slot);
        var timeout = TimeSpan.FromSeconds(Math.Max(30, instruction.TimeoutSeconds));
        if (DateTime.UtcNow - slot.LoadCommandSentUtc.Value < timeout)
            return false;

        reason = $"Character-load timeout for slot {slot.SlotId}; active character did not become {slot.RequiredCharacterKey}.";
        return true;
    }

    private static DadSchedulerPresetPhase ResolveWaitingPhase(IReadOnlyList<DadSchedulerSlotState> slots)
    {
        if (slots.Any(static slot => slot.LoadCommandSentUtc.HasValue && !slot.Ready))
            return DadSchedulerPresetPhase.LoadingCharacters;

        if (slots.Any(static slot => slot.LaunchStarted && !slot.Ready))
            return DadSchedulerPresetPhase.WaitingForHeartbeat;

        return slots.Any(static slot => slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline && !slot.IsOnline)
            ? DadSchedulerPresetPhase.LaunchingClients
            : DadSchedulerPresetPhase.Resolving;
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
        currentState.Phase = DadSchedulerPresetPhase.Blocked;
        currentState.Summary = string.IsNullOrWhiteSpace(reason) ? "Scheduler blocked." : reason;
        currentState.BlockedReason = currentState.Summary;
        currentState.CompletedAtUtc = DateTime.UtcNow;
        currentState.UpdatedAtUtc = DateTime.UtcNow;
        RecordTerminalResult(currentState);
    }

    private DadScheduledCrewJob? FindEquivalentActiveOrPendingJob(DadSchedulerJobType jobType, string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        if (activeJob != null &&
            currentState.IsActive &&
            activeJob.JobType == jobType &&
            string.Equals(activeJob.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
        {
            return activeJob;
        }

        return configuration.SchedulerQueue.FirstOrDefault(job =>
            job.JobType == jobType &&
            string.Equals(job.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
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
            or DadSchedulerPresetPhase.Cancelled;

    private static bool IsSuccessfulTerminalPhase(DadSchedulerPresetPhase phase)
        => phase is DadSchedulerPresetPhase.StartedPlanner or DadSchedulerPresetPhase.Completed;

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
            BlockActiveScheduleRun($"Schedule '{state.ScheduleName}' could not be resolved.");
            return;
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
            BlockActiveScheduleRun($"Schedule '{state.ScheduleName}' stopped after '{state.CurrentPresetName}' ended with {run.Status}: {detail}");
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
                BlockActiveScheduleRun($"Schedule '{state.ScheduleName}' stopped: {FormatScheduleText(result.BlockedReason, result.Summary)}");
                return;
            }

            if (string.IsNullOrWhiteSpace(state.ActivePlannerRequestId) &&
                string.Equals(currentState.JobId, state.ActiveSchedulerJobId, StringComparison.OrdinalIgnoreCase))
            {
                state.ActivePlannerRequestId = currentState.PlannerRequestId;
            }

            if (string.IsNullOrWhiteSpace(state.ActivePlannerRequestId))
            {
                BlockActiveScheduleRun($"Schedule '{state.ScheduleName}' stopped: scheduler job did not produce a Dad run id.");
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
                BlockActiveScheduleRun($"Schedule '{state.ScheduleName}' stopped: {FormatScheduleText(currentState.BlockedReason, currentState.Summary)}");
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

        BlockActiveScheduleRun($"Schedule '{state.ScheduleName}' stopped: scheduler job {state.ActiveSchedulerJobId} disappeared before producing a result.");
    }

    private void MaterializeScheduleEntryJob(DadScheduleDefinition schedule, DadScheduleRunState state, DateTime now)
    {
        var groupIds = BuildPlannerGroupIdSet();
        var blocker = DadScheduleRules.ValidateCurrentEntry(state, schedule.Entries, groupIds);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            BlockActiveScheduleRun(blocker);
            return;
        }

        var entry = DadScheduleRules.GetCurrentEntry(state, schedule.Entries);
        if (entry == null)
        {
            BlockActiveScheduleRun($"Schedule '{state.ScheduleName}' has no entry at index {state.CurrentEntryIndex + 1}.");
            return;
        }

        var group = FindPlannerGroup(entry.GroupId);
        if (group == null)
        {
            BlockActiveScheduleRun($"Schedule entry {state.CurrentEntryIndex + 1} references missing preset '{entry.GroupId}'.");
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
        if (!manualRun)
            schedule.LastDailyResetUtc = DadScheduleRules.GetDailyResetBoundaryUtc(now);

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

    private void BlockActiveScheduleRun(string reason)
    {
        configuration.ActiveScheduleRun ??= new DadScheduleRunState();
        configuration.ActiveScheduleRun = DadScheduleRules.BlockRun(configuration.ActiveScheduleRun, reason, DateTime.UtcNow);
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
        });
    }

    private void RecordTerminalResult(DadSchedulerPresetState state)
    {
        if (!IsTerminalPhase(state.Phase))
            return;

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
        });
    }

    private void RecordTerminalResult(DadScheduledCrewJobResult result)
    {
        NormalizeHistory();
        if (string.IsNullOrWhiteSpace(result.JobId))
            result.JobId = Guid.NewGuid().ToString("N");

        configuration.SchedulerHistory.RemoveAll(existing =>
            string.Equals(existing.JobId, result.JobId, StringComparison.OrdinalIgnoreCase));
        configuration.SchedulerHistory.Insert(0, result.Clone());
        TrimSchedulerHistory();
        configuration.Save();
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
        configuration.ActiveScheduleRun.RepeatIteration = Math.Max(1, configuration.ActiveScheduleRun.RepeatIteration);
        configuration.ActiveScheduleRun.TotalEntryExecutions = Math.Max(0, configuration.ActiveScheduleRun.TotalEntryExecutions);
        configuration.ActiveScheduleRun.CompletedEntryExecutions = Math.Clamp(
            configuration.ActiveScheduleRun.CompletedEntryExecutions,
            0,
            Math.Max(configuration.ActiveScheduleRun.TotalEntryExecutions, configuration.ActiveScheduleRun.CompletedEntryExecutions));
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
