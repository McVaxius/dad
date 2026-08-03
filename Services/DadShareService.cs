using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using dad.Models;

namespace dad.Services;

public sealed class DadShareService
{
    private const int MaxTextLength = 512;
    private const int MaxCommandLength = 2_048;
    private const int MaxCommands = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Func<string> randomTokenFactory;
    private readonly Func<string, string> forceKrangle;

    public DadShareService(
        Func<string>? randomTokenFactory = null,
        Func<string, string>? forceKrangle = null)
    {
        this.randomTokenFactory = randomTokenFactory ?? CreateRandomToken;
        this.forceKrangle = forceKrangle ?? FallbackKrangle;
    }

    public bool TryExportPlan(
        DadPlannerGroup plan,
        IEnumerable<DadShareKnownIdentity>? knownIdentities,
        out string encoded,
        out string error)
        => TryExportPlan(plan, knownIdentities, null, out encoded, out error);

    public bool TryExportPlan(
        DadPlannerGroup plan,
        IEnumerable<DadShareKnownIdentity>? knownIdentities,
        DadCompletionActions? completionFallback,
        out string encoded,
        out string error)
    {
        encoded = string.Empty;
        error = string.Empty;
        if (plan == null)
            return Fail("Select a saved Plan before exporting.", out error);

        var privacy = new PrivacySession(this, knownIdentities);
        var envelope = new DadShareEnvelopeDto
        {
            Kind = DadShareConstants.PlanKind,
            Plan = BuildPlanDto(plan, privacy, completionFallback),
        };
        return TryEncodeEnvelope(envelope, out encoded, out error);
    }

    public bool TryExportSchedule(
        DadScheduleDefinition schedule,
        IEnumerable<DadPlannerGroup>? availablePlans,
        IEnumerable<DadShareKnownIdentity>? knownIdentities,
        out string encoded,
        out string error)
        => TryExportSchedule(schedule, availablePlans, knownIdentities, null, out encoded, out error);

    public bool TryExportSchedule(
        DadScheduleDefinition schedule,
        IEnumerable<DadPlannerGroup>? availablePlans,
        IEnumerable<DadShareKnownIdentity>? knownIdentities,
        DadCompletionActions? completionFallback,
        out string encoded,
        out string error)
    {
        encoded = string.Empty;
        error = string.Empty;
        if (schedule == null)
            return Fail("Select a saved Schedule before exporting.", out error);

        var plans = (availablePlans ?? []).Where(static plan => plan != null).ToList();
        var duplicatePlanId = plans
            .GroupBy(static plan => plan.GroupId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicatePlanId != null)
            return Fail($"Plan ID '{duplicatePlanId.Key}' is duplicated; repair it before exporting a Schedule.", out error);

        var referencedPlans = new List<DadPlannerGroup>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in schedule.Entries ?? [])
        {
            var matches = plans
                .Where(plan => string.Equals(plan.GroupId, entry.GroupId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                return Fail(
                    $"Schedule entry '{entry.EntryId}' references missing Plan '{entry.GroupId}'. Export was not created.",
                    out error);
            }

            if (seen.Add(matches[0].GroupId))
                referencedPlans.Add(matches[0]);
        }

        var privacy = new PrivacySession(this, knownIdentities);
        var envelope = new DadShareEnvelopeDto
        {
            Kind = DadShareConstants.ScheduleKind,
            Schedule = BuildScheduleDto(schedule, privacy),
            Plans = referencedPlans.Select(plan => BuildPlanDto(plan, privacy, completionFallback)).ToList(),
        };
        return TryEncodeEnvelope(envelope, out encoded, out error);
    }

    public bool TryEncodeEnvelope(
        DadShareEnvelopeDto envelope,
        out string encoded,
        out string error)
    {
        encoded = string.Empty;
        if (envelope == null)
            return Fail("Share payload is empty.", out error);
        if (!TryValidateEnvelope(envelope, envelope.Kind, out error))
            return false;

        try
        {
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (jsonBytes.Length > DadShareConstants.MaxDecodedBytes)
                return Fail("Share payload is too large.", out error);

            encoded = Convert.ToBase64String(jsonBytes);
            if (encoded.Length > DadShareConstants.MaxEncodedCharacters)
            {
                encoded = string.Empty;
                return Fail("Share payload is too large.", out error);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Fail("Share payload could not be encoded.", out error);
        }
    }

    public bool TryDecode(
        string encoded,
        string expectedKind,
        out DadShareEnvelopeDto? envelope,
        out string error)
    {
        envelope = null;
        error = string.Empty;
        var trimmed = encoded?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return Fail("Clipboard does not contain a DAD share payload.", out error);
        if (trimmed.Length > DadShareConstants.MaxEncodedCharacters)
            return Fail("Clipboard share payload is too large.", out error);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return Fail("Clipboard text is not valid Base64.", out error);
        }

        if (bytes.Length > DadShareConstants.MaxDecodedBytes)
            return Fail("Decoded share payload is too large.", out error);

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Fail("Share payload is not valid UTF-8.", out error);
        }

        try
        {
            envelope = JsonSerializer.Deserialize<DadShareEnvelopeDto>(json, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Fail("Share payload is not valid DAD JSON.", out error);
        }

        if (!TryValidateEnvelope(envelope, expectedKind, out error))
        {
            envelope = null;
            return false;
        }

        return true;
    }

    public bool TryValidateEnvelope(
        DadShareEnvelopeDto? envelope,
        string? expectedKind,
        out string error)
    {
        error = string.Empty;
        if (envelope == null)
            return Fail("Share payload is empty.", out error);
        if (!string.Equals(envelope.Format, DadShareConstants.Format, StringComparison.Ordinal))
            return Fail($"Unknown share format '{envelope.Format}'.", out error);
        if (envelope.Schema is < DadShareConstants.MinimumSupportedSchema or > DadShareConstants.Schema)
            return Fail($"Unsupported share schema {envelope.Schema}.", out error);
        if (envelope.Kind is not DadShareConstants.PlanKind and not DadShareConstants.ScheduleKind)
            return Fail($"Unknown share kind '{envelope.Kind}'.", out error);
        if (!string.IsNullOrWhiteSpace(expectedKind) &&
            !string.Equals(envelope.Kind, expectedKind, StringComparison.Ordinal))
        {
            return Fail($"Clipboard contains a {envelope.Kind} share, not a {expectedKind} share.", out error);
        }

        envelope.Plans ??= [];
        if (envelope.Kind == DadShareConstants.PlanKind)
        {
            if (envelope.Plan == null || envelope.Schedule != null || envelope.Plans.Count != 0)
                return Fail("Plan share envelope has an invalid shape.", out error);
            return TryValidatePlan(envelope.Plan, out error);
        }

        if (envelope.Plan != null || envelope.Schedule == null)
            return Fail("Schedule share envelope has an invalid shape.", out error);
        if (envelope.Plans.Count > DadShareConstants.MaxBundledPlans)
            return Fail("Schedule share contains too many bundled Plans.", out error);

        var duplicatePlanId = envelope.Plans
            .GroupBy(static plan => plan?.GroupId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicatePlanId != null)
            return Fail($"Bundled Plan ID '{duplicatePlanId.Key}' is duplicated.", out error);
        foreach (var plan in envelope.Plans)
        {
            if (!TryValidatePlan(plan, out error))
                return false;
        }

        if (!TryValidateSchedule(envelope.Schedule, envelope.Plans, out error))
            return false;
        return true;
    }

    public DadShareImportPreview BuildImportPreview(
        DadShareEnvelopeDto envelope,
        IEnumerable<DadPlannerGroup>? currentPlans,
        IEnumerable<DadScheduleDefinition>? currentSchedules)
    {
        var plans = (currentPlans ?? []).ToList();
        var schedules = (currentSchedules ?? []).ToList();
        var replacements = new List<string>();
        if (envelope.Kind == DadShareConstants.PlanKind && envelope.Plan != null)
        {
            if (plans.Any(plan => string.Equals(plan.GroupId, envelope.Plan.GroupId, StringComparison.OrdinalIgnoreCase)))
                replacements.Add(envelope.Plan.GroupId);
            return new DadShareImportPreview
            {
                Kind = envelope.Kind,
                Name = envelope.Plan.DisplayName,
                Id = envelope.Plan.GroupId,
                BundledPlanCount = 1,
                ReplacementIds = replacements,
                Commands = BuildCommandPreview([envelope.Plan]),
            };
        }

        foreach (var plan in envelope.Plans)
        {
            if (plans.Any(current => string.Equals(current.GroupId, plan.GroupId, StringComparison.OrdinalIgnoreCase)))
                replacements.Add(plan.GroupId);
        }
        if (envelope.Schedule != null && schedules.Any(schedule =>
                string.Equals(schedule.ScheduleId, envelope.Schedule.ScheduleId, StringComparison.OrdinalIgnoreCase)))
        {
            replacements.Add(envelope.Schedule.ScheduleId);
        }

        return new DadShareImportPreview
        {
            Kind = envelope.Kind,
            Name = envelope.Schedule?.DisplayName ?? string.Empty,
            Id = envelope.Schedule?.ScheduleId ?? string.Empty,
            BundledPlanCount = envelope.Plans.Count,
            ReplacementIds = replacements,
            Commands = BuildCommandPreview(envelope.Plans),
        };
    }

    private static List<DadShareCommandPreviewItem> BuildCommandPreview(
        IEnumerable<DadSharePlanDto> plans)
    {
        var commands = new List<DadShareCommandPreviewItem>();
        foreach (var plan in plans)
        {
            var actions = plan.CompletionActions;
            if (actions == null)
                continue;
            foreach (var command in actions.Commands ?? [])
            {
                if (!string.IsNullOrWhiteSpace(command))
                {
                    commands.Add(new DadShareCommandPreviewItem
                    {
                        PlanName = plan.DisplayName,
                        CommandKind = "CustomCommand",
                        Command = command,
                    });
                }
            }

            var grandCompanyCommand = actions.Utilities?.GrandCompanyHandInCommand;
            if (!string.IsNullOrWhiteSpace(grandCompanyCommand))
            {
                commands.Add(new DadShareCommandPreviewItem
                {
                    PlanName = plan.DisplayName,
                    CommandKind = "GrandCompanyHandInCommand",
                    Command = grandCompanyCommand,
                });
            }
        }
        return commands;
    }

    public DadShareApplyResult Apply(
        DadShareEnvelopeDto envelope,
        IEnumerable<DadPlannerGroup>? currentPlans,
        IEnumerable<DadScheduleDefinition>? currentSchedules,
        DadShareApplyMode mode = DadShareApplyMode.ReplaceMatching,
        bool commandValuesConfirmed = false)
    {
        if (envelope == null)
            return new DadShareApplyResult { Summary = "Share payload is empty." };
        if (!TryValidateEnvelope(envelope, envelope.Kind, out var validationError))
            return new DadShareApplyResult { Summary = validationError };
        if (BuildImportPreview(envelope, currentPlans, currentSchedules).RequiresCommandConfirmation &&
            !commandValuesConfirmed)
        {
            return new DadShareApplyResult
            {
                Summary = "Review and explicitly confirm every imported CustomCommand and GrandCompanyHandInCommand value before applying this share.",
            };
        }

        var sourcePlans = (currentPlans ?? []).Where(static plan => plan != null).ToList();
        var sourceSchedules = (currentSchedules ?? []).Where(static schedule => schedule != null).ToList();
        var duplicatePlan = sourcePlans
            .GroupBy(static plan => plan.GroupId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicatePlan != null)
            return new DadShareApplyResult { Summary = $"Existing Plan ID '{duplicatePlan.Key}' is duplicated; import was not applied." };
        var duplicateSchedule = sourceSchedules
            .GroupBy(static schedule => schedule.ScheduleId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateSchedule != null)
            return new DadShareApplyResult { Summary = $"Existing Schedule ID '{duplicateSchedule.Key}' is duplicated; import was not applied." };

        var plans = sourcePlans.Select(ClonePlan).ToList();
        var schedules = sourceSchedules.Select(static schedule => schedule.Clone()).ToList();
        var result = new DadShareApplyResult
        {
            PlannerGroups = plans,
            Schedules = schedules,
        };
        var transferPlans = envelope.Kind == DadShareConstants.PlanKind
            ? [envelope.Plan!]
            : envelope.Plans;
        var now = DateTime.UtcNow;

        // Plans are deliberately applied before their owning Schedule.
        foreach (var transfer in transferPlans)
        {
            var existingIndex = plans.FindIndex(plan =>
                string.Equals(plan.GroupId, transfer.GroupId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0 && mode == DadShareApplyMode.SkipExisting)
            {
                result.SkippedPlanCount++;
                continue;
            }

            var existing = existingIndex >= 0 ? plans[existingIndex] : null;
            var materialized = MaterializePlan(transfer, existing, now);
            if (existingIndex >= 0)
            {
                plans[existingIndex] = materialized;
                result.ReplacedPlanCount++;
            }
            else
            {
                plans.Add(materialized);
                result.AddedPlanCount++;
            }
        }

        if (envelope.Kind == DadShareConstants.ScheduleKind && envelope.Schedule != null)
        {
            var existingIndex = schedules.FindIndex(schedule =>
                string.Equals(schedule.ScheduleId, envelope.Schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0 && mode == DadShareApplyMode.SkipExisting)
            {
                result.ScheduleSkipped = true;
            }
            else
            {
                var existing = existingIndex >= 0 ? schedules[existingIndex] : null;
                var materialized = MaterializeSchedule(envelope.Schedule, existing, now);
                if (existingIndex >= 0)
                {
                    schedules[existingIndex] = materialized;
                    result.ScheduleReplaced = true;
                }
                else
                {
                    schedules.Add(materialized);
                    result.ScheduleAdded = true;
                }
            }
            result.ResultId = envelope.Schedule.ScheduleId;
        }
        else
        {
            result.ResultId = envelope.Plan!.GroupId;
        }

        result.Success = true;
        result.Summary = mode == DadShareApplyMode.SkipExisting
            ? $"Starter install added {result.AddedPlanCount} Plan(s) and {(result.ScheduleAdded ? 1 : 0)} Schedule; skipped {result.SkippedPlanCount + (result.ScheduleSkipped ? 1 : 0)} existing component(s)."
            : envelope.Kind == DadShareConstants.PlanKind
                ? $"Imported Plan '{envelope.Plan!.DisplayName}'."
                : $"Imported Schedule '{envelope.Schedule!.DisplayName}' with {envelope.Plans.Count} bundled Plan(s).";
        return result;
    }

    public DadShareRenameResult RenamePlanId(
        IList<DadPlannerGroup> plans,
        IList<DadScheduleDefinition> schedules,
        IList<DadScheduledCrewJob> pendingJobs,
        DadPresetPlannerOptions plannerOptions,
        string currentId,
        string requestedId)
    {
        if (!TryNormalizeCanonicalId(requestedId, out var newId))
            return RenameFailure("ID must be a canonical lowercase 32-hex GUID.");
        var matches = plans.Where(plan => string.Equals(plan.GroupId, currentId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            return RenameFailure(matches.Count == 0 ? "Plan ID was not found." : "Current Plan ID is duplicated.");
        if (plans.Any(plan => !ReferenceEquals(plan, matches[0]) && string.Equals(plan.GroupId, newId, StringComparison.OrdinalIgnoreCase)))
            return RenameFailure($"Plan ID '{newId}' already exists.");
        if (string.Equals(matches[0].GroupId, newId, StringComparison.Ordinal))
            return new DadShareRenameResult { Success = true, NewId = newId, Summary = "Plan ID is unchanged." };

        var oldId = matches[0].GroupId;
        var count = 0;
        matches[0].GroupId = newId;
        count++;
        if (string.Equals(plannerOptions.SelectedPlannerGroupId, oldId, StringComparison.OrdinalIgnoreCase))
        {
            plannerOptions.SelectedPlannerGroupId = newId;
            count++;
        }
        foreach (var entry in schedules.SelectMany(static schedule => schedule.Entries))
        {
            if (!string.Equals(entry.GroupId, oldId, StringComparison.OrdinalIgnoreCase))
                continue;
            entry.GroupId = newId;
            count++;
        }
        foreach (var job in pendingJobs)
        {
            if (!string.Equals(job.GroupId, oldId, StringComparison.OrdinalIgnoreCase))
                continue;
            job.GroupId = newId;
            count++;
        }

        return new DadShareRenameResult
        {
            Success = true,
            NewId = newId,
            UpdatedReferenceCount = count,
            Summary = $"Plan ID changed to {newId}; updated {count - 1} mutable reference(s).",
        };
    }

    public DadShareRenameResult RenameScheduleId(
        IList<DadScheduleDefinition> schedules,
        IList<DadScheduledCrewJob> pendingJobs,
        DadScheduleRunState activeScheduleRun,
        string currentId,
        string requestedId)
    {
        if (!TryNormalizeCanonicalId(requestedId, out var newId))
            return RenameFailure("ID must be a canonical lowercase 32-hex GUID.");
        var matches = schedules.Where(schedule => string.Equals(schedule.ScheduleId, currentId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            return RenameFailure(matches.Count == 0 ? "Schedule ID was not found." : "Current Schedule ID is duplicated.");
        if (schedules.Any(schedule => !ReferenceEquals(schedule, matches[0]) && string.Equals(schedule.ScheduleId, newId, StringComparison.OrdinalIgnoreCase)))
            return RenameFailure($"Schedule ID '{newId}' already exists.");
        if (string.Equals(matches[0].ScheduleId, newId, StringComparison.Ordinal))
            return new DadShareRenameResult { Success = true, NewId = newId, Summary = "Schedule ID is unchanged." };

        var oldId = matches[0].ScheduleId;
        var count = 0;
        matches[0].ScheduleId = newId;
        count++;
        foreach (var job in pendingJobs)
        {
            if (!string.Equals(job.ScheduleId, oldId, StringComparison.OrdinalIgnoreCase))
                continue;
            job.ScheduleId = newId;
            count++;
        }
        if (string.Equals(activeScheduleRun.ScheduleId, oldId, StringComparison.OrdinalIgnoreCase))
        {
            activeScheduleRun.ScheduleId = newId;
            count++;
        }

        return new DadShareRenameResult
        {
            Success = true,
            NewId = newId,
            UpdatedReferenceCount = count,
            Summary = $"Schedule ID changed to {newId}; updated {count - 1} mutable reference(s).",
        };
    }

    public static bool TryNormalizeCanonicalId(string value, out string canonical)
    {
        canonical = string.Empty;
        var trimmed = value?.Trim() ?? string.Empty;
        if (!Guid.TryParseExact(trimmed, "N", out var parsed) || parsed == Guid.Empty)
            return false;
        canonical = parsed.ToString("N");
        return string.Equals(trimmed, canonical, StringComparison.Ordinal);
    }

    public static string GetMutationBlocker(bool dadRunActive, bool schedulerActive, bool scheduleActive)
        => dadRunActive
            ? "Sharing changes are locked while DAD work is active."
            : schedulerActive
                ? "Sharing changes are locked while scheduler work is active."
                : scheduleActive
                    ? "Sharing changes are locked while a Schedule is active."
                    : string.Empty;

    private DadSharePlanDto BuildPlanDto(
        DadPlannerGroup source,
        PrivacySession privacy,
        DadCompletionActions? completionFallback)
    {
        var slots = (source.Slots ?? []).Select(slot => BuildSlotDto(slot, privacy)).ToList();
        var stop = source.StopPolicy ?? new DadRunStopPolicy();
        var targetToken = string.Empty;
        var targetLabel = string.Empty;
        if (stop.Mode == DadPlannerStopMode.TargetLevel)
        {
            if (!string.IsNullOrWhiteSpace(source.SharedStopTargetIdentityToken))
            {
                targetToken = privacy.GetCharacterToken($"shared:{source.SharedStopTargetIdentityToken}");
                targetLabel = slots.FirstOrDefault(slot => string.Equals(slot.CharacterToken, targetToken, StringComparison.Ordinal))?.CharacterLabel
                              ?? privacy.Krangle(stop.TargetCharacterLabel);
            }
            else if (!stop.TargetCharacterKey.IsEmpty)
            {
                targetToken = privacy.GetCharacterToken($"local:{stop.TargetCharacterKey.Value}");
                targetLabel = privacy.Krangle(string.IsNullOrWhiteSpace(stop.TargetCharacterLabel)
                    ? stop.TargetCharacterKey.Value
                    : stop.TargetCharacterLabel);
            }
            else if (!string.IsNullOrWhiteSpace(stop.TargetCharacterLabel))
            {
                targetLabel = privacy.Krangle(stop.TargetCharacterLabel);
            }
        }

        return new DadSharePlanDto
        {
            GroupId = source.GroupId?.Trim() ?? string.Empty,
            DisplayName = privacy.Sanitize(source.DisplayName),
            RunFamily = source.RunFamily,
            ActivityMode = source.ActivityMode,
            OperatorMode = source.OperatorMode,
            ConnectedOnly = source.ConnectedOnly,
            SameDatacenterOnly = source.SameDatacenterOnly,
            AllowStaleForPlanning = source.AllowStaleForPlanning,
            TransportOwner = source.TransportOwner,
            QueueAuthority = source.QueueAuthority,
            InviteAuthority = source.InviteAuthority,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = privacy.Sanitize(source.DutyDisplayName),
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            RouletteTarget = new DadShareQueueTargetDto
            {
                Kind = source.RouletteTarget?.Kind ?? DadQueueTargetKind.Roulette,
                ContentFinderConditionId = source.RouletteTarget?.ContentFinderConditionId ?? 0,
                RouletteId = source.RouletteTarget?.RouletteId ?? 0,
                Key = privacy.Sanitize(source.RouletteTarget?.Key),
                DisplayName = privacy.Sanitize(source.RouletteTarget?.DisplayName),
            },
            MogtomePreset = privacy.Sanitize(source.MogtomePreset),
            MogtomeDutyPolicy = privacy.Sanitize(source.MogtomeDutyPolicy),
            RefreshTrustNpcLevels = source.RefreshTrustNpcLevels,
            StopPolicy = new DadShareStopPolicyDto
            {
                Mode = stop.Mode,
                AfterRuns = stop.AfterRuns,
                TargetLevel = stop.TargetLevel,
                TargetCharacterToken = targetToken,
                TargetCharacterLabel = targetLabel,
                SafetyCap = stop.SafetyCap,
                StopItemId = stop.StopItemId,
                StopItemTargetCount = stop.StopItemTargetCount,
            },
            LevelingMode = new DadShareLevelingModeDto
            {
                Enabled = source.LevelingMode?.Enabled ?? false,
                GoalLevel = source.LevelingMode?.GoalLevel ?? DadRunStopPolicy.DefaultTargetLevel,
                JobOrder = source.LevelingMode?.JobOrder ?? DadLevelingJobOrder.LowestFirst,
                DutyThresholds = (source.LevelingMode?.DutyThresholds ?? [])
                    .Where(static threshold => threshold != null)
                    .Select(threshold => new DadShareLevelingDutyThresholdDto
                    {
                        MinimumLevel = threshold.MinimumLevel,
                        ContentFinderConditionId = threshold.ContentFinderConditionId,
                        DutyDisplayName = privacy.Sanitize(threshold.DutyDisplayName),
                    })
                    .ToList(),
            },
            CompletionActions = BuildCompletionActionsDto(source.CompletionActions ?? completionFallback),
            Slots = slots,
            IsTemplate = source.IsTemplate,
            MapRunTemplate = privacy.Sanitize(source.MapRunTemplate),
            MapMode = source.MapMode,
        };
    }

    private static DadShareCompletionActionsDto? BuildCompletionActionsDto(DadCompletionActions? source)
        => source == null
            ? null
            : new DadShareCompletionActionsDto
            {
                PlaySound = source.PlaySound,
                SoundEffectId = source.SoundEffectId,
                RunCommands = source.RunCommands,
                // Finish commands are intentionally verbatim. Base64 is transport encoding, not encryption.
                Commands = source.Commands == null ? [] : [..source.Commands],
                KillMode = source.KillMode,
                Utilities = new DadSharePostRunUtilitiesDto
                {
                    OpenGearCoffers = source.Utilities?.OpenGearCoffers ?? false,
                    RegisterTripleTriadCards = source.Utilities?.RegisterTripleTriadCards ?? false,
                    SellTripleTriadCards = source.Utilities?.SellTripleTriadCards ?? false,
                    GrandCompanyHandInViaAutoRetainer = source.Utilities?.GrandCompanyHandInViaAutoRetainer ?? false,
                    GrandCompanyHandInCommand = source.Utilities?.GrandCompanyHandInCommand ?? "/ays gc",
                },
            };

    private DadSharePlanSlotDto BuildSlotDto(DadPlannerGroupSlot source, PrivacySession privacy)
    {
        string accountToken;
        string characterToken;
        string characterLabel;
        if (source.SharedIdentity is { } placeholder)
        {
            accountToken = privacy.GetAccountToken($"shared:{placeholder.AccountToken}");
            characterToken = placeholder.RequiresCharacter
                ? privacy.GetCharacterToken($"shared:{placeholder.IdentityToken}")
                : string.Empty;
            characterLabel = placeholder.RequiresCharacter ? placeholder.CharacterLabel : string.Empty;
        }
        else
        {
            accountToken = source.RequiredAccountKey.IsEmpty
                ? string.Empty
                : privacy.GetAccountToken($"local:{source.RequiredAccountKey.Value}");
            characterToken = source.RequiredCharacterKey.IsEmpty
                ? string.Empty
                : privacy.GetCharacterToken($"local:{source.RequiredCharacterKey.Value}");
            characterLabel = source.RequiredCharacterKey.IsEmpty
                ? string.Empty
                : privacy.Krangle(source.RequiredCharacterKey.Value);
        }

        return new DadSharePlanSlotDto
        {
            SlotId = source.SlotId?.Trim() ?? string.Empty,
            IsSubstitute = source.IsSubstitute,
            AllianceAssignment = source.AllianceAssignment,
            RequiredRole = source.RequiredRole,
            AccountToken = accountToken,
            CharacterToken = characterToken,
            CharacterLabel = characterLabel,
            RequiredJobId = source.RequiredJobId,
            AdsLootMode = source.AdsLootMode,
            LevelSeekTarget = source.LevelSeekTarget,
            SkipIfDailyRouletteRewardReceived = source.SkipIfDailyRouletteRewardReceived,
            WakePolicy = source.WakePolicy,
            AllowSubstitution = source.AllowSubstitution,
        };
    }

    private static DadShareScheduleDto BuildScheduleDto(DadScheduleDefinition source, PrivacySession privacy)
        => new()
        {
            ScheduleId = source.ScheduleId?.Trim() ?? string.Empty,
            DisplayName = privacy.Sanitize(source.DisplayName),
            Cadence = source.Cadence,
            Entries = (source.Entries ?? []).Select(entry => new DadShareScheduleEntryDto
            {
                EntryId = entry.EntryId?.Trim() ?? string.Empty,
                GroupId = entry.GroupId?.Trim() ?? string.Empty,
                PresetName = privacy.Sanitize(entry.PresetName),
                RepeatCount = entry.RepeatCount,
            }).ToList(),
        };

    private static DadPlannerGroup MaterializePlan(
        DadSharePlanDto source,
        DadPlannerGroup? existing,
        DateTime now)
    {
        var group = new DadPlannerGroup
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
            InviteAuthority = source.InviteAuthority,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = source.DutyDisplayName,
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            RouletteTarget = new DadQueueTarget
            {
                Kind = source.RouletteTarget.Kind,
                ContentFinderConditionId = source.RouletteTarget.ContentFinderConditionId,
                RouletteId = source.RouletteTarget.RouletteId,
                Key = source.RouletteTarget.Key,
                DisplayName = source.RouletteTarget.DisplayName,
            },
            MogtomePreset = source.MogtomePreset,
            MogtomeDutyPolicy = source.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = source.RefreshTrustNpcLevels,
            StopPolicy = new DadRunStopPolicy
            {
                Mode = source.StopPolicy.Mode,
                AfterRuns = source.StopPolicy.AfterRuns,
                TargetLevel = source.StopPolicy.TargetLevel,
                TargetCharacterKey = new DadCharacterKey(string.Empty),
                TargetCharacterLabel = source.StopPolicy.TargetCharacterLabel,
                SafetyCap = source.StopPolicy.SafetyCap,
                StopItemId = source.StopPolicy.StopItemId,
                StopItemTargetCount = source.StopPolicy.StopItemTargetCount,
            }.Normalize(),
            LevelingMode = new DadLevelingModeOptions
            {
                Enabled = source.LevelingMode?.Enabled ?? false,
                GoalLevel = source.LevelingMode?.GoalLevel ?? DadRunStopPolicy.DefaultTargetLevel,
                JobOrder = source.LevelingMode?.JobOrder ?? DadLevelingJobOrder.LowestFirst,
                DutyThresholds = (source.LevelingMode?.DutyThresholds ?? [])
                    .Where(static threshold => threshold != null)
                    .Select(static threshold => new DadLevelingDutyThreshold
                    {
                        MinimumLevel = threshold.MinimumLevel,
                        ContentFinderConditionId = threshold.ContentFinderConditionId,
                        DutyDisplayName = threshold.DutyDisplayName,
                    })
                    .ToList(),
            }.Normalize(),
            SharedStopTargetIdentityToken = source.StopPolicy.TargetCharacterToken,
            CompletionActions = MaterializeCompletionActions(source.CompletionActions),
            Slots = source.Slots.Select(slot => new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                IsSubstitute = slot.IsSubstitute,
                AllianceAssignment = slot.AllianceAssignment,
                RequiredRole = slot.RequiredRole,
                RequiredAccountKey = new DadAccountKey(string.Empty),
                RequiredCharacterKey = new DadCharacterKey(string.Empty),
                RequiredJobId = slot.RequiredJobId,
                AdsLootMode = slot.AdsLootMode,
                LevelSeekTarget = slot.LevelSeekTarget,
                SkipIfDailyRouletteRewardReceived = slot.SkipIfDailyRouletteRewardReceived,
                WakePolicy = slot.WakePolicy,
                LaunchProfileId = string.Empty,
                CharacterLoadInstruction = new DadCharacterLoadInstruction(),
                SharedIdentity = string.IsNullOrWhiteSpace(slot.AccountToken) && string.IsNullOrWhiteSpace(slot.CharacterToken)
                    ? null
                    : new DadSharedIdentityPlaceholder
                    {
                        IdentityToken = string.IsNullOrWhiteSpace(slot.CharacterToken)
                            ? $"account-only:{slot.AccountToken}:{slot.SlotId}:{slot.IsSubstitute}"
                            : slot.CharacterToken,
                        AccountToken = slot.AccountToken,
                        CharacterLabel = slot.CharacterLabel,
                        RequiresCharacter = !string.IsNullOrWhiteSpace(slot.CharacterToken),
                    },
                AllowSubstitution = slot.AllowSubstitution,
            }).ToList(),
            IsTemplate = source.IsTemplate,
            MapRunTemplate = source.MapRunTemplate,
            MapMode = source.MapMode,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
            ScheduleEnabled = existing?.ScheduleEnabled ?? false,
            ScheduleCadenceHours = existing?.ScheduleCadenceHours ?? 0,
            NextEligibleTimeUtc = existing?.NextEligibleTimeUtc,
            ScheduleRequester = existing?.ScheduleRequester ?? string.Empty,
            SchedulePriority = existing?.SchedulePriority ?? 0,
        };

        PreserveMachineLocalSlotFields(group.Slots, existing?.Slots);
        group.Slots = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots);
        return group;
    }

    private static DadCompletionActions? MaterializeCompletionActions(DadShareCompletionActionsDto? source)
        => source == null
            ? null
            : new DadCompletionActions
            {
                PlaySound = source.PlaySound,
                SoundEffectId = source.SoundEffectId,
                RunCommands = source.RunCommands,
                Commands = source.Commands == null ? [] : [..source.Commands],
                KillMode = source.KillMode,
                Utilities = new DadPostRunUtilities
                {
                    OpenGearCoffers = source.Utilities.OpenGearCoffers,
                    RegisterTripleTriadCards = source.Utilities.RegisterTripleTriadCards,
                    SellTripleTriadCards = source.Utilities.SellTripleTriadCards,
                    GrandCompanyHandInViaAutoRetainer = source.Utilities.GrandCompanyHandInViaAutoRetainer,
                    GrandCompanyHandInCommand = source.Utilities.GrandCompanyHandInCommand,
                },
            };

    private static void PreserveMachineLocalSlotFields(
        IReadOnlyList<DadPlannerGroupSlot> incoming,
        IEnumerable<DadPlannerGroupSlot>? existing)
    {
        var existingRows = DadPlannerSlotRules.NormalizeGroupSlots(existing).ToList();
        var occurrenceByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in incoming)
        {
            var key = $"{slot.SlotId}|{slot.IsSubstitute}";
            occurrenceByKey.TryGetValue(key, out var occurrence);
            occurrenceByKey[key] = occurrence + 1;
            var local = existingRows
                .Where(candidate => string.Equals(candidate.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase) &&
                                    candidate.IsSubstitute == slot.IsSubstitute)
                .Skip(occurrence)
                .FirstOrDefault();
            if (local == null)
                continue;
            slot.LaunchProfileId = local.LaunchProfileId;
            slot.CharacterLoadInstruction = local.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction();
        }
    }

    private static DadScheduleDefinition MaterializeSchedule(
        DadShareScheduleDto source,
        DadScheduleDefinition? existing,
        DateTime now)
        => new DadScheduleDefinition
        {
            SchemaVersion = existing?.SchemaVersion ?? 1,
            Revision = existing == null ? 1 : existing.Revision + 1,
            ScheduleId = source.ScheduleId,
            DisplayName = source.DisplayName,
            Cadence = source.Cadence,
            Entries = source.Entries.Select(entry => new DadScheduleEntry
            {
                EntryId = entry.EntryId,
                GroupId = entry.GroupId,
                PresetName = entry.PresetName,
                RepeatCount = entry.RepeatCount,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }).ToList(),
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
            LastDailyResetUtc = existing?.LastDailyResetUtc,
            LastRunStartedAtUtc = existing?.LastRunStartedAtUtc,
            LastRunCompletedAtUtc = existing?.LastRunCompletedAtUtc,
            LastRunStatus = existing?.LastRunStatus ?? DadScheduleRunStatus.Idle,
            LastSummary = existing?.LastSummary ?? string.Empty,
        }.Normalize();

    private static DadPlannerGroup ClonePlan(DadPlannerGroup source)
    {
        var clone = DadSchedulerGroupCloneRules.CloneWithSlots(source, source.Slots ?? []);
        clone.InviteAuthority = source.InviteAuthority;
        return clone;
    }

    private static bool TryValidatePlan(DadSharePlanDto? plan, out string error)
    {
        error = string.Empty;
        if (plan == null)
            return Fail("Share contains a missing Plan.", out error);
        if (!TryNormalizeCanonicalId(plan.GroupId, out _))
            return Fail($"Plan ID '{plan.GroupId}' is not a canonical 32-hex GUID.", out error);
        if (!ValidateText(plan.DisplayName, "Plan name", 128, allowEmpty: false, out error))
            return false;
        if (!IsDefined(plan.RunFamily) || !IsDefined(plan.ActivityMode) || !IsDefined(plan.OperatorMode) ||
            !IsDefined(plan.TransportOwner) || !IsDefined(plan.QueueAuthority) || !IsDefined(plan.InviteAuthority) ||
            !IsDefined(plan.MapMode))
        {
            return Fail("Plan contains an undefined enum value.", out error);
        }
        if (plan.DutyExpectedPartySize is < 1 or > 48)
            return Fail("Plan party size is invalid.", out error);
        if (!ValidateText(plan.DutyDisplayName, "Duty name", MaxTextLength, true, out error) ||
            !ValidateText(plan.MogtomePreset, "MOGTOME preset", MaxTextLength, true, out error) ||
            !ValidateText(plan.MogtomeDutyPolicy, "MOGTOME duty policy", MaxTextLength, true, out error) ||
            !ValidateText(plan.MapRunTemplate, "Map template", MaxTextLength, true, out error))
        {
            return false;
        }
        if (plan.RouletteTarget == null || !IsDefined(plan.RouletteTarget.Kind))
            return Fail("Plan roulette target is invalid.", out error);
        if (!ValidateText(plan.RouletteTarget.Key, "Roulette key", MaxTextLength, true, out error) ||
            !ValidateText(plan.RouletteTarget.DisplayName, "Roulette name", MaxTextLength, true, out error))
        {
            return false;
        }
        if (plan.StopPolicy == null || !IsDefined(plan.StopPolicy.Mode))
            return Fail("Plan stop policy is invalid.", out error);
        if (plan.StopPolicy.AfterRuns is < 1 or > 200 ||
            plan.StopPolicy.TargetLevel is < 1 or > 999 ||
            plan.StopPolicy.SafetyCap is < 1 or > 200 ||
            plan.StopPolicy.StopItemTargetCount is < 1 or > 99_999)
        {
            return Fail("Plan stop policy contains an invalid count.", out error);
        }
        if (!ValidateText(plan.StopPolicy.TargetCharacterToken, "Stop target token", 192, true, out error) ||
            !ValidateText(plan.StopPolicy.TargetCharacterLabel, "Stop target label", 192, true, out error))
        {
            return false;
        }
        if (plan.LevelingMode == null || !IsDefined(plan.LevelingMode.JobOrder))
            return Fail("Plan Leveling Mode settings are invalid.", out error);
        if (plan.LevelingMode.GoalLevel is < 1 or > 999)
            return Fail("Plan Leveling Mode goal is invalid.", out error);
        if (plan.LevelingMode.DutyThresholds == null || plan.LevelingMode.DutyThresholds.Count > 256)
            return Fail("Plan contains too many Leveling Mode duty thresholds.", out error);
        foreach (var threshold in plan.LevelingMode.DutyThresholds)
        {
            if (threshold == null || threshold.MinimumLevel is < 1 or > 999 || threshold.ContentFinderConditionId == 0)
                return Fail("Plan contains an invalid Leveling Mode duty threshold.", out error);
            if (!ValidateText(threshold.DutyDisplayName, "Leveling Mode duty name", MaxTextLength, true, out error))
                return false;
        }
        if (plan.Slots == null || plan.Slots.Count > DadShareConstants.MaxSlotsPerPlan)
            return Fail("Plan contains too many crew rows.", out error);

        var characterIdentities = new Dictionary<string, (string Account, string Label)>(StringComparer.Ordinal);
        var primaryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var availablePrimaryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in plan.Slots)
        {
            if (slot == null)
                return Fail("Plan contains an empty crew row.", out error);
            var normalizedSlotId = DadPlannerSlotRules.NormalizeStrictSlotId(slot.SlotId);
            if (string.IsNullOrWhiteSpace(normalizedSlotId) || !string.Equals(slot.SlotId, normalizedSlotId, StringComparison.Ordinal))
                return Fail($"Crew row ID '{slot.SlotId}' is invalid.", out error);
            if (!IsDefined(slot.RequiredRole) || !IsDefined(slot.AdsLootMode) || !IsDefined(slot.WakePolicy))
                return Fail($"Crew row '{slot.SlotId}' contains an undefined enum value.", out error);
            if (!slot.IsSubstitute)
            {
                if (!primaryIds.Add(slot.SlotId))
                    return Fail($"Crew primary row '{slot.SlotId}' is duplicated.", out error);
                availablePrimaryIds.Add(slot.SlotId);
            }
            else if (!availablePrimaryIds.Contains(slot.SlotId))
            {
                return Fail($"Crew substitute row '{slot.SlotId}' has no preceding primary row.", out error);
            }
            if (slot.RequiredJobId is > 1_000)
                return Fail($"Crew row '{slot.SlotId}' has an invalid job ID.", out error);
            if (slot.LevelSeekTarget.HasValue && slot.LevelSeekTarget is < 1 or > 999)
                return Fail($"Crew row '{slot.SlotId}' has an invalid Level seek target.", out error);
            if (!ValidateText(slot.AccountToken, "Account token", 192, true, out error) ||
                !ValidateText(slot.CharacterToken, "Character token", 192, true, out error) ||
                !ValidateText(slot.CharacterLabel, "Character label", 192, true, out error))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(slot.CharacterToken) &&
                (string.IsNullOrWhiteSpace(slot.AccountToken) || string.IsNullOrWhiteSpace(slot.CharacterLabel)))
            {
                return Fail($"Crew row '{slot.SlotId}' has an incomplete shared character identity.", out error);
            }
            if (string.IsNullOrWhiteSpace(slot.CharacterToken) && !string.IsNullOrWhiteSpace(slot.CharacterLabel))
                return Fail($"Crew row '{slot.SlotId}' has a character label without an identity token.", out error);
            if (!string.IsNullOrWhiteSpace(slot.CharacterToken))
            {
                var identity = (slot.AccountToken, slot.CharacterLabel);
                if (characterIdentities.TryGetValue(slot.CharacterToken, out var prior) && prior != identity)
                    return Fail($"Character token '{slot.CharacterToken}' has conflicting identity data.", out error);
                characterIdentities[slot.CharacterToken] = identity;
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.StopPolicy.TargetCharacterToken) &&
            !characterIdentities.ContainsKey(plan.StopPolicy.TargetCharacterToken))
        {
            return Fail("Shared stop target does not resolve to a bundled crew row.", out error);
        }
        return TryValidateCompletionActions(plan.CompletionActions, out error);
    }

    private static bool TryValidateCompletionActions(DadShareCompletionActionsDto? actions, out string error)
    {
        error = string.Empty;
        if (actions == null)
            return true;
        if (!IsDefined(actions.KillMode))
            return Fail("Finish actions contain an undefined enum value.", out error);
        if (actions.SoundEffectId is < 1 or > 16)
            return Fail("Finish sound effect ID is invalid.", out error);
        if (actions.Commands == null || actions.Commands.Count > MaxCommands)
            return Fail("Finish actions contain too many commands.", out error);
        foreach (var command in actions.Commands)
        {
            if (!ValidateText(command, "Finish command", MaxCommandLength, true, out error))
                return false;
            if (!string.IsNullOrWhiteSpace(command) &&
                !DadCompletionCommandRules.TryNormalizeCustomCommand(command, out _, out error))
                return false;
        }
        if (actions.Utilities == null ||
            !ValidateText(actions.Utilities.GrandCompanyHandInCommand, "Grand Company command", MaxCommandLength, true, out error))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(actions.Utilities.GrandCompanyHandInCommand) &&
            !DadCompletionCommandRules.TryNormalizeGrandCompanyHandInCommand(
                actions.Utilities.GrandCompanyHandInCommand,
                out _,
                out error))
            return false;
        return true;
    }

    private static bool TryValidateSchedule(
        DadShareScheduleDto schedule,
        IReadOnlyList<DadSharePlanDto> plans,
        out string error)
    {
        error = string.Empty;
        if (!TryNormalizeCanonicalId(schedule.ScheduleId, out _))
            return Fail($"Schedule ID '{schedule.ScheduleId}' is not a canonical 32-hex GUID.", out error);
        if (!ValidateText(schedule.DisplayName, "Schedule name", 128, false, out error))
            return false;
        if (!IsDefined(schedule.Cadence))
            return Fail("Schedule contains an undefined cadence.", out error);
        if (schedule.Entries == null || schedule.Entries.Count > DadShareConstants.MaxScheduleEntries)
            return Fail("Schedule contains too many entries.", out error);

        var entryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in schedule.Entries)
        {
            if (entry == null)
                return Fail("Schedule contains an empty entry.", out error);
            if (!TryNormalizeCanonicalId(entry.EntryId, out _))
                return Fail($"Schedule entry ID '{entry.EntryId}' is invalid.", out error);
            if (!entryIds.Add(entry.EntryId))
                return Fail($"Schedule entry ID '{entry.EntryId}' is duplicated.", out error);
            if (!TryNormalizeCanonicalId(entry.GroupId, out _))
                return Fail($"Schedule entry Plan ID '{entry.GroupId}' is invalid.", out error);
            if (entry.RepeatCount is < DadScheduleRules.MinRepeatCount or > DadScheduleRules.MaxRepeatCount)
                return Fail($"Schedule entry '{entry.EntryId}' has an invalid repeat count.", out error);
            if (!ValidateText(entry.PresetName, "Schedule entry name", 128, true, out error))
                return false;
            referencedPlans.Add(entry.GroupId);
        }

        var bundledIds = plans.Select(static plan => plan.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = referencedPlans.FirstOrDefault(id => !bundledIds.Contains(id));
        if (!string.IsNullOrWhiteSpace(missing))
            return Fail($"Schedule references unresolved bundled Plan '{missing}'.", out error);
        var extra = bundledIds.FirstOrDefault(id => !referencedPlans.Contains(id));
        if (!string.IsNullOrWhiteSpace(extra))
            return Fail($"Schedule bundle contains unreferenced Plan '{extra}'.", out error);
        return true;
    }

    private static bool ValidateText(
        string? value,
        string label,
        int maxLength,
        bool allowEmpty,
        out string error)
    {
        if (value == null)
            return Fail($"{label} must not be null.", out error);
        var text = value;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            return Fail($"{label} is required.", out error);
        if (text.Length > maxLength)
            return Fail($"{label} is too long.", out error);
        error = string.Empty;
        return true;
    }

    private static bool IsDefined<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => Enum.IsDefined(value);

    private static DadShareRenameResult RenameFailure(string summary)
        => new() { Summary = summary };

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string CreateRandomToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string FallbackKrangle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"Shared-{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
    }

    private sealed class PrivacySession
    {
        private readonly DadShareService service;
        private readonly List<DadShareKnownIdentity> knownIdentities;
        private readonly Dictionary<string, string> accountTokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> characterTokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> krangledValues = new(StringComparer.Ordinal);
        private readonly HashSet<string> issuedTokens = new(StringComparer.Ordinal);

        public PrivacySession(DadShareService service, IEnumerable<DadShareKnownIdentity>? knownIdentities)
        {
            this.service = service;
            this.knownIdentities = (knownIdentities ?? [])
                .Where(static identity => identity != null)
                .ToList();
        }

        public string GetAccountToken(string source)
            => GetToken(accountTokens, source, "acct");

        public string GetCharacterToken(string source)
            => GetToken(characterTokens, source, "char");

        public string Krangle(string? value)
        {
            var text = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return text;
            if (!krangledValues.TryGetValue(text, out var krangled))
            {
                krangled = service.forceKrangle(text);
                krangledValues[text] = krangled;
            }
            return krangled;
        }

        public string Sanitize(string? value)
        {
            var output = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(output))
                return output;

            var replacements = new List<(string Original, string Replacement)>();
            foreach (var identity in knownIdentities)
            {
                var accountSource = !string.IsNullOrWhiteSpace(identity.AccountKey)
                    ? $"local:{identity.AccountKey}"
                    : $"alias:{identity.AccountAlias}";
                var accountToken = string.IsNullOrWhiteSpace(identity.AccountKey) && string.IsNullOrWhiteSpace(identity.AccountAlias)
                    ? string.Empty
                    : GetAccountToken(accountSource);
                AddReplacement(replacements, identity.AccountKey, accountToken);
                AddReplacement(replacements, identity.AccountAlias, accountToken);
                AddReplacement(replacements, identity.CharacterKey, Krangle(identity.CharacterKey));
                AddReplacement(replacements, identity.CharacterName, Krangle(identity.CharacterName));
            }

            foreach (var replacement in replacements
                         .Where(static replacement => !string.IsNullOrWhiteSpace(replacement.Original))
                         .OrderByDescending(static replacement => replacement.Original.Length))
            {
                output = output.Replace(
                    replacement.Original,
                    replacement.Replacement,
                    StringComparison.OrdinalIgnoreCase);
            }
            return output;
        }

        private string GetToken(Dictionary<string, string> tokens, string source, string prefix)
        {
            if (string.IsNullOrWhiteSpace(source))
                return string.Empty;
            if (tokens.TryGetValue(source, out var existing))
                return existing;

            for (var attempts = 0; attempts < 16; attempts++)
            {
                var candidate = $"{prefix}-{service.randomTokenFactory()}";
                if (!issuedTokens.Add(candidate))
                    continue;

                tokens[source] = candidate;
                return candidate;
            }

            var token = $"{prefix}-{Guid.NewGuid():N}";
            issuedTokens.Add(token);
            tokens[source] = token;
            return token;
        }

        private static void AddReplacement(
            ICollection<(string Original, string Replacement)> replacements,
            string original,
            string replacement)
        {
            if (!string.IsNullOrWhiteSpace(original) && !string.IsNullOrWhiteSpace(replacement))
                replacements.Add((original.Trim(), replacement));
        }
    }
}

public static class DadStarterShareBundle
{
    public const string ScheduleId = "64264bb48bfa47d9850969c58218da16";
    public const string LevelingPlanId = "4b797a7cfed94226a37b693051a16823";
    public const string MainScenarioPlanId = "176d559e307148a08d7d5ac1711ff54e";
    public const string LevelingEntryId = "b3242780e96b4c97b4c83c337aaa0a53";
    public const string MainScenarioEntryId = "602770f7f641480784f17256882b481b";

    public static bool TryCreateEncoded(DadShareService shareService, out string encoded, out string error)
    {
        var leveling = BuildPlan(
            LevelingPlanId,
            "Leveling Roulette Daily",
            rouletteId: 1,
            rouletteName: "Duty Roulette: Leveling");
        var mainScenario = BuildPlan(
            MainScenarioPlanId,
            "MSQ Daily",
            rouletteId: 3,
            rouletteName: "Duty Roulette: Main Scenario");
        var schedule = new DadScheduleDefinition
        {
            ScheduleId = ScheduleId,
            DisplayName = "Daily MSQ + Leveling",
            Cadence = DadScheduleCadence.Manual,
            Entries =
            [
                new DadScheduleEntry
                {
                    EntryId = LevelingEntryId,
                    GroupId = LevelingPlanId,
                    PresetName = leveling.DisplayName,
                    RepeatCount = 1,
                },
                new DadScheduleEntry
                {
                    EntryId = MainScenarioEntryId,
                    GroupId = MainScenarioPlanId,
                    PresetName = mainScenario.DisplayName,
                    RepeatCount = 1,
                },
            ],
        };
        return shareService.TryExportSchedule(schedule, [leveling, mainScenario], [], out encoded, out error);
    }

    private static DadPlannerGroup BuildPlan(
        string id,
        string name,
        uint rouletteId,
        string rouletteName)
        => new()
        {
            GroupId = id,
            DisplayName = name,
            RunFamily = DadPlannerRunFamily.DailyRoulette,
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            OperatorMode = DadPlannerOperatorMode.RemotePartyPlan,
            ConnectedOnly = true,
            SameDatacenterOnly = true,
            AllowStaleForPlanning = false,
            TransportOwner = DadTransportOwner.LanParty,
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyExpectedPartySize = 4,
            RouletteTarget = new DadQueueTarget
            {
                Kind = DadQueueTargetKind.Roulette,
                RouletteId = rouletteId,
                Key = $"ContentRoulette:{rouletteId}",
                DisplayName = rouletteName,
            },
            MogtomePreset = "Daily MSQ",
            MogtomeDutyPolicy = DadMogtomeDutyPolicies.PresetHandoff,
            RefreshTrustNpcLevels = true,
            StopPolicy = new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.AfterRuns,
                AfterRuns = 1,
                TargetLevel = 47,
                SafetyCap = 20,
            },
            CompletionActions = new DadCompletionActions(),
            Slots = BuildAnonymousCrew(),
            MapMode = DadMapCrewJobMode.GatherThenRun,
        };

    private static List<DadPlannerGroupSlot> BuildAnonymousCrew()
        =>
        [
            BuildSlot(1, null, 100),
            BuildSlot(2, 32, null),
            BuildSlot(3, 28, null),
            BuildSlot(4, 38, null),
        ];

    private static DadPlannerGroupSlot BuildSlot(int slotNumber, uint? jobId, int? levelSeek)
        => new()
        {
            SlotId = DadPlannerSlotRules.FormatSlotId(slotNumber),
            RequiredRole = DadPartyRole.Any,
            RequiredAccountKey = new DadAccountKey($"starter-account-{slotNumber}"),
            RequiredCharacterKey = slotNumber == 1
                ? new DadCharacterKey(string.Empty)
                : new DadCharacterKey($"Starter Crew {slotNumber}@Shared"),
            RequiredJobId = jobId,
            AdsLootMode = DadAdsLootMode.NoChange,
            LevelSeekTarget = levelSeek,
            WakePolicy = DadSchedulerWakePolicy.LaunchIfOffline,
            AllowSubstitution = false,
        };
}
