using System.Diagnostics;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadSchedulerService
{
    private const string ClientBootDirectory = @"Z:\!ff14clientboot";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly Configuration configuration;
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
        DadCharacterIntelligenceService characterIntelligenceService,
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadRosterCatalogService rosterCatalogService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.characterIntelligenceService = characterIntelligenceService;
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.rosterCatalogService = rosterCatalogService;
        this.log = log;
    }

    public DadSchedulerPresetState CurrentState => currentState.Clone();

    public DadSchedulerQueueSnapshot GetQueueSnapshot()
    {
        NormalizeQueue();
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
            Summary = currentState.IsActive
                ? $"Active {currentState.JobType}: {currentState.Summary}"
                : configuration.SchedulerQueue.Count == 0
                    ? "Scheduler queue idle."
                    : $"{configuration.SchedulerQueue.Count} queued scheduler job(s).",
        };
    }

    public DadScheduledCrewJob EnqueueScheduledPreset(DadPlannerGroup group, DadScheduledPresetRequest request)
    {
        NormalizeQueue();
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
            StatusSummary = $"Queued preset '{group.DisplayName}'.",
        };

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
        var targets = catalog.Characters
            .Where(character =>
                character.Visibility == DadRosterVisibility.NeedsUpdate ||
                plan.CharacterRefs.Any(reference => DadRosterIdentity.Matches(character, reference)) ||
                plan.CharacterKeys.Any(key => string.Equals(key.Value, character.CharacterKey.Value, StringComparison.OrdinalIgnoreCase)) ||
                plan.AccountKeys.Any(key => DadRosterIdentity.SameAccount(key, character.AccountKey)))
            .Where(character => !character.CharacterKey.IsEmpty)
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

    public IReadOnlyList<DadLaunchProfile> GetLaunchProfiles()
    {
        NormalizeLaunchProfiles();
        return configuration.LaunchProfiles.Select(static profile => profile.Clone()).ToList();
    }

    public int ImportLaunchProfilesFromBootDirectory()
    {
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

        if (!plannerRequestPreview.CanStart || plannerRequestPreview.Request == null)
        {
            BlockPreview(preview, string.IsNullOrWhiteSpace(plannerRequestPreview.BlockedReason)
                ? plannerRequestPreview.StatusSummary
                : plannerRequestPreview.BlockedReason);
            preview.Slots = BuildSlotStates(group, pool, currentState.Slots);
            return preview;
        }

        preview.Slots = BuildSlotStates(group, pool, currentState.Slots);
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
        };

        if (!preview.CanStart)
        {
            currentState.Phase = DadSchedulerPresetPhase.Blocked;
            currentState.Summary = $"Scheduler blocked for preset '{group.DisplayName}'.";
            currentState.BlockedReason = preview.BlockedReason;
            currentState.CompletedAtUtc = DateTime.UtcNow;
            return CurrentState;
        }

        if (dryRun)
        {
            currentState.Phase = DadSchedulerPresetPhase.Completed;
            currentState.Summary = $"Scheduler dry run ready for preset '{group.DisplayName}': {preview.StatusSummary}";
            currentState.CompletedAtUtc = DateTime.UtcNow;
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
        Func<DadRunRequest, DadRunResult> startPlannerRequest)
    {
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
        var preview = plannerPreviewBuilder(nextJob.GroupId);
        if (group == null || preview == null)
        {
            activeJob = nextJob.Clone();
            currentState = BuildBlockedQueuedState(nextJob, $"Queued preset '{nextJob.GroupId}' could not be resolved.");
            return true;
        }

        StartPreset(group, preview, nextJob.DryRun, nextJob);
        return true;
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

        currentState.Slots = BuildSlotStates(group, pool, [], allowNeedsUpdateVisibility: true);
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
            return;
        }

        if (job.DryRun)
        {
            currentState.Phase = DadSchedulerPresetPhase.Completed;
            currentState.Summary = $"Roster update dry run ready for {targets.Count} character(s).";
            currentState.CompletedAtUtc = DateTime.UtcNow;
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
        currentState.Slots = BuildSlotStates(group, pool, currentState.Slots, allowNeedsUpdateVisibility: true);
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
                BlockActive($"No roster refresh acknowledgement from {slot.RequiredCharacterKey}.");
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
        characterIntelligenceService.RefreshLocalCharacterPool("roster-update", logRefresh: false);
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
            .Where(character => character.Visibility == DadRosterVisibility.NeedsUpdate || hasExplicitTargets)
            .Where(static character => !character.CharacterKey.IsEmpty)
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
            allowNeedsUpdateVisibility: currentState.JobType == DadSchedulerJobType.RosterUpdate);

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

    private List<DadSchedulerSlotState> BuildSlotStates(
        DadPlannerGroup group,
        DadCharacterPool pool,
        IReadOnlyList<DadSchedulerSlotState> previousSlots,
        bool allowNeedsUpdateVisibility = false)
    {
        var participants = BuildParticipantSet(pool);
        return group.Slots.Select(slot =>
        {
            var previous = previousSlots.FirstOrDefault(existing =>
                string.Equals(existing.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
            var state = BuildSlotState(slot, participants, allowNeedsUpdateVisibility);
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
        bool allowNeedsUpdateVisibility = false)
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
        if (state.RosterVisibility is DadRosterVisibility.Hidden or DadRosterVisibility.Ignored ||
            state.RosterVisibility == DadRosterVisibility.NeedsUpdate && !allowNeedsUpdateVisibility)
        {
            state.BlockedReason = $"Slot {state.SlotId} targets {state.RosterVisibility} roster character {slot.RequiredCharacterKey}.";
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
            if (!profile.Enabled)
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' is disabled.";
            else if (!profile.AllowAutoStart)
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' does not allow auto-start.";
            else if (string.IsNullOrWhiteSpace(profile.BatchPath))
                state.BlockedReason = $"Launch profile '{profile.DisplayName}' has no batch path.";
            else if (!File.Exists(profile.BatchPath))
                state.BlockedReason = $"Launch profile batch path not found: {profile.BatchPath}.";
            else
                state.Summary = profile.DryRun
                    ? $"Dry-run launch profile would start {profile.BatchPath} for account {profile.AccountKey}."
                    : $"Launch profile ready: {profile.BatchPath} for account {profile.AccountKey}.";
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

            state.Summary = instruction.DryRun
                ? $"Dry-run would send character-load command for {slot.RequiredCharacterKey}: {command}"
                : $"Character-load command ready for {slot.RequiredCharacterKey}.";
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

            state.Summary = instruction.DryRun
                ? $"Dry-run would send character-load command for {slot.RequiredCharacterKey}: {command}"
                : $"Character-load command ready for {slot.RequiredCharacterKey}.";
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

        var accountProfile = configuration.LaunchProfiles.FirstOrDefault(profile =>
            !slot.RequiredAccountKey.IsEmpty &&
            string.Equals(profile.AccountKey.Value, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase));
        if (accountProfile != null || !slot.RequiredAccountKey.IsEmpty)
            return accountProfile;

        return configuration.LaunchProfiles.FirstOrDefault(profile =>
                   !slot.RequiredCharacterKey.IsEmpty &&
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

        var accountProfile = configuration.LaunchProfiles.FirstOrDefault(profile =>
            !slot.RequiredAccountKey.IsEmpty &&
            string.Equals(profile.AccountKey.Value, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase));
        if (accountProfile != null || !slot.RequiredAccountKey.IsEmpty)
            return accountProfile;

        return configuration.LaunchProfiles.FirstOrDefault(profile =>
                   !slot.RequiredCharacterKey.IsEmpty &&
                   profile.ExpectedCharacterKeys.Any(key =>
                       string.Equals(key.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase)));
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
        if (profile.DryRun)
        {
            blocker = $"Launch profile '{profile.DisplayName}' is dry-run only; would start {profile.BatchPath}.";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = profile.BatchPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(profile.BatchPath) ?? string.Empty,
            };
            Process.Start(startInfo);
            slot.LaunchStarted = true;
            slot.LaunchStartedUtc = DateTime.UtcNow;
            slot.Summary = $"Started launch profile '{profile.DisplayName}' for account {profile.AccountKey}; waiting for Dad heartbeat.";
            currentState.Phase = DadSchedulerPresetPhase.WaitingForHeartbeat;
            log.Information("[dad][Scheduler] Started launch profile {ProfileId} ({BatchPath}) for slot {SlotId}.",
                profile.ProfileId,
                profile.BatchPath,
                slot.SlotId);
            return true;
        }
        catch (Exception ex)
        {
            blocker = $"Failed to start launch profile '{profile.DisplayName}': {ex.Message}";
            log.Warning(ex, "[dad][Scheduler] {Blocker}", blocker);
            return false;
        }
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
            blocker = result?.Summary ?? $"No character-load acknowledgement from {participant.ActiveCharacterKey}.";
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
        foreach (var job in configuration.SchedulerQueue)
        {
            job.JobId = string.IsNullOrWhiteSpace(job.JobId) ? Guid.NewGuid().ToString("N") : job.JobId.Trim();
            job.PresetName = job.PresetName?.Trim() ?? string.Empty;
            job.GroupId = job.GroupId?.Trim() ?? string.Empty;
            job.RequestedBy = string.IsNullOrWhiteSpace(job.RequestedBy) ? "scheduler" : job.RequestedBy.Trim();
            job.MapRunTemplate = job.MapRunTemplate?.Trim() ?? string.Empty;
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
