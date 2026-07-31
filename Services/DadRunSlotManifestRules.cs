using dad.Models;

namespace dad.Services;

internal static class DadRunSlotManifestRules
{
    public static bool RequiresFrozenRoster(DadRunPlan plan)
        => plan.RequiredParticipantCount > 1 ||
           plan.RequiresRemoteParticipants ||
           (plan.Orchestration?.RequiredRosterCharacters?.Any(static reference => reference.RequiredJobId.HasValue) ?? false);

    public static bool TryCreate(
        DadRunPlan plan,
        out DadRunSlotManifest manifest,
        out string blocker)
    {
        manifest = new DadRunSlotManifest();
        blocker = string.Empty;

        if (!RequiresFrozenRoster(plan))
            return true;

        if (plan.Request == null ||
            plan.Orchestration == null ||
            plan.Orchestration.RosterIntent == null ||
            plan.Request.Orchestration == null ||
            plan.Request.Orchestration.RosterIntent == null ||
            plan.Modules == null)
        {
            return Fail("Frozen-roster run is missing its typed plan, orchestration, roster intent, or module payload.", out blocker);
        }

        if (string.IsNullOrWhiteSpace(plan.Request.RequestId))
            return Fail("Frozen-roster run is missing a request id.", out blocker);

        var isMultiplayer = plan.RequiredParticipantCount > 1 || plan.RequiresRemoteParticipants;
        if (isMultiplayer && (plan.RequiredParticipantCount < 2 || !plan.RequiresRemoteParticipants))
            return Fail("Multiplayer run has contradictory remote-participant requirements.", out blocker);

        var roster = plan.Orchestration.RequiredRosterCharacters ?? [];
        var requestRoster = plan.Request.Orchestration.RequiredRosterCharacters ?? [];
        if (!SameOrderedRoster(roster, requestRoster) ||
            plan.Request.Orchestration.RosterIntent.ExpectedPartySize != plan.Orchestration.RosterIntent.ExpectedPartySize)
        {
            return Fail("Run plan orchestration contradicts the request's ordered typed roster or party size.", out blocker);
        }

        if (roster.Count != plan.RequiredParticipantCount)
        {
            return Fail(
                $"Frozen-roster run requires a complete typed roster: expected {plan.RequiredParticipantCount} ordered character(s), received {roster.Count}.",
                out blocker);
        }

        if (plan.Orchestration.RosterIntent.ExpectedPartySize != plan.RequiredParticipantCount)
        {
            return Fail(
                $"Frozen-roster run party-size contradiction: roster intent is {plan.Orchestration.RosterIntent.ExpectedPartySize}, plan requires {plan.RequiredParticipantCount}.",
                out blocker);
        }

        if (isMultiplayer)
        {
            if (plan.Orchestration.InviteAuthority is DadInviteAuthority.External or DadInviteAuthority.NotNeeded)
                return Fail("Multiplayer frozen roster requires exact Slot1 invite authority.", out blocker);
            if (string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) ||
                string.IsNullOrWhiteSpace(plan.InviterCharacterKey) ||
                !Same(plan.LeaderCharacterKey, plan.InviterCharacterKey))
            {
                return Fail(
                    $"Frozen leader '{plan.LeaderCharacterKey}' and inviter '{plan.InviterCharacterKey}' must both be exact Slot1.",
                    out blocker);
            }
        }

        var slots = new List<DadFrozenRunSlot>(roster.Count);
        for (var index = 0; index < roster.Count; index++)
        {
            var reference = roster[index];
            var slotId = DadPlannerSlotRules.FormatSlotId(index + 1);
            if (reference.AccountKey.IsEmpty || reference.CharacterKey.IsEmpty || reference.ContentId == 0)
            {
                return Fail(
                    $"{slotId} requires an exact managed account, character key, and non-zero Content ID before multiplayer acceptance.",
                    out blocker);
            }

            if (reference.RequiredJobId is 0 ||
                (reference.RequiredJobId.HasValue && !DadRosterCharacterMerge.IsCombatJob(reference.RequiredJobId.Value)))
            {
                return Fail(
                    $"{slotId} requested class/job {reference.RequiredJobId.GetValueOrDefault()} is not a positive combat job.",
                    out blocker);
            }

            slots.Add(new DadFrozenRunSlot
            {
                SlotId = slotId,
                AccountKey = reference.AccountKey,
                CharacterKey = reference.CharacterKey,
                ContentId = reference.ContentId,
                RequiredJobId = reference.RequiredJobId,
                AdsLootMode = reference.AdsLootMode,
                IsLeader = index == 0,
                IsInviter = index == 0 && isMultiplayer,
            });
        }

        var duplicateAccount = slots
            .GroupBy(static slot => Normalize(slot.AccountKey.Value), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateAccount != null)
            return Fail($"Managed account '{duplicateAccount.First().AccountKey}' is assigned to more than one frozen slot.", out blocker);

        var duplicateCharacter = slots
            .GroupBy(static slot => Normalize(slot.CharacterKey.Value), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateCharacter != null)
            return Fail($"Character '{duplicateCharacter.First().CharacterKey}' is assigned to more than one frozen slot.", out blocker);

        var duplicateContentId = slots
            .GroupBy(static slot => slot.ContentId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateContentId != null)
            return Fail($"Content ID {duplicateContentId.Key} is assigned to more than one frozen slot.", out blocker);

        if (!Same(slots[0].CharacterKey.Value, plan.LeaderCharacterKey) ||
            !slots[0].IsLeader ||
            slots.Count(static slot => slot.IsLeader) != 1)
            return Fail($"Slot1 must be the one exact queue leader '{plan.LeaderCharacterKey}'.", out blocker);

        if (isMultiplayer &&
            (!Same(slots[0].CharacterKey.Value, plan.InviterCharacterKey) ||
             !slots[0].IsInviter ||
             slots.Count(static slot => slot.IsInviter) != 1))
        {
            return Fail($"Party inviter '{plan.InviterCharacterKey}' must be the one exact frozen Slot1.", out blocker);
        }

        var payloads = new List<DadFrozenModulePayload>(plan.Modules.Count);
        foreach (var module in plan.Modules)
        {
            DadFrozenModulePayload payload;
            if (plan.Orchestration.AutoPartyFormationOnly)
            {
                payload = new DadFrozenModulePayload
                {
                    ModuleId = module.ModuleId,
                    ExpectedPartySize = module.ExpectedPartySize,
                };
            }
            else if (!TryBuildModulePayload(plan.Request, module, out payload, out blocker))
            {
                return false;
            }

            if (module.RequiresPeers && module.ExpectedPartySize != plan.RequiredParticipantCount)
            {
                return Fail(
                    $"{module.DisplayName} expects {module.ExpectedPartySize} participant(s), but the frozen roster contains {plan.RequiredParticipantCount}.",
                    out blocker);
            }

            if (module.ExpectedPartySize > 1 && !module.RequiresPeers)
                return Fail($"{module.DisplayName} has party size {module.ExpectedPartySize} but is not marked as a peer module.", out blocker);

            payloads.Add(payload);
        }

        if (isMultiplayer && payloads.All(static payload => payload.ExpectedPartySize <= 1))
            return Fail("Multiplayer run has no multiplayer module payload.", out blocker);

        manifest = new DadRunSlotManifest
        {
            RequestId = plan.Request.RequestId,
            ExpectedPartySize = plan.RequiredParticipantCount,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            InviterCharacterKey = plan.InviterCharacterKey,
            Modules = payloads,
            Slots = slots,
        };
        return true;
    }

    public static bool TryBindWorkerSessions(
        DadRunSlotManifest source,
        IReadOnlyList<DadParticipantSnapshot> onlineParticipants,
        out DadRunSlotManifest bound,
        out string blocker)
    {
        bound = source.Clone();
        blocker = string.Empty;
        var assignedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in bound.Slots)
        {
            var matches = onlineParticipants
                .Where(participant =>
                    !participant.WorkerSessionId.IsEmpty &&
                    Same(participant.ManagedAccountKey.Value, slot.AccountKey.Value))
                .DistinctBy(static participant => Normalize(participant.WorkerSessionId.Value), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matches.Count != 1)
            {
                blocker = matches.Count == 0
                    ? $"{slot.SlotId} cannot bind account '{slot.AccountKey}' to an online Dad worker session."
                    : $"{slot.SlotId} account '{slot.AccountKey}' maps to {matches.Count} online Dad worker sessions.";
                return false;
            }

            var session = matches[0].WorkerSessionId;
            if (!assignedSessions.Add(Normalize(session.Value)))
            {
                blocker = $"Worker session '{session}' is assigned to more than one frozen slot.";
                return false;
            }

            slot.WorkerSessionId = session;
        }

        return true;
    }

    public static DadParticipantSnapshot ResolveSlot(
        DadFrozenRunSlot slot,
        IReadOnlyList<DadParticipantSnapshot> currentParticipants,
        bool requirePostArReady,
        out string blocker)
    {
        blocker = string.Empty;
        var matches = currentParticipants
            .Where(participant => Same(participant.WorkerSessionId.Value, slot.WorkerSessionId.Value))
            .ToList();
        if (matches.Count != 1)
        {
            blocker = matches.Count == 0
                ? $"{slot.SlotId} is waiting for frozen worker session '{slot.WorkerSessionId}' on account '{slot.AccountKey}'."
                : $"{slot.SlotId} found duplicate runtime rows for frozen worker session '{slot.WorkerSessionId}'.";
            return BuildWaitingSnapshot(slot, DadParticipantState.Stale, blocker);
        }

        var resolved = matches[0].Clone();
        resolved.AssignedSlotId = slot.SlotId;
        resolved.DesiredCharacterKey = slot.CharacterKey.Value;

        if (!Same(resolved.ManagedAccountKey.Value, slot.AccountKey.Value))
        {
            blocker = $"{slot.SlotId} worker '{slot.WorkerSessionId}' has account '{resolved.ManagedAccountKey}', expected exact account '{slot.AccountKey}'.";
            return MarkWaiting(resolved, DadParticipantState.WaitingForRequiredCharacter, blocker);
        }

        if (!Same(resolved.ActiveCharacterKey.Value, slot.CharacterKey.Value) ||
            resolved.Character.ContentId != slot.ContentId)
        {
            blocker = $"{slot.SlotId} requires '{slot.CharacterKey}' Content ID {slot.ContentId} on account '{slot.AccountKey}'; " +
                      $"worker '{slot.WorkerSessionId}' has '{resolved.ActiveCharacterKey}' Content ID {resolved.Character.ContentId}.";
            return MarkWaiting(resolved, DadParticipantState.WaitingForRequiredCharacter, blocker);
        }

        if (!resolved.IsAvailable || !resolved.IsEligibleForRun || resolved.State == DadParticipantState.Stale)
        {
            blocker = $"{slot.SlotId} exact worker '{slot.WorkerSessionId}' for '{slot.CharacterKey}' is unavailable or stale.";
            return MarkWaiting(resolved, DadParticipantState.Stale, blocker);
        }

        if (requirePostArReady && !resolved.PostArReady)
        {
            blocker = $"{slot.SlotId} exact character '{slot.CharacterKey}' is waiting for post-AR readiness on worker '{slot.WorkerSessionId}'.";
            return MarkWaiting(resolved, DadParticipantState.WaitingForPostArReady, blocker);
        }

        resolved.State = DadParticipantState.Discovered;
        resolved.StatusText = $"{slot.SlotId} exact frozen assignment resolved on worker {slot.WorkerSessionId}.";
        return resolved;
    }

    private static DadParticipantSnapshot BuildWaitingSnapshot(
        DadFrozenRunSlot slot,
        DadParticipantState state,
        string blocker)
        => new()
        {
            WorkerSessionId = slot.WorkerSessionId,
            ManagedAccountKey = slot.AccountKey,
            ActiveCharacterKey = new DadCharacterKey(string.Empty),
            Character = new DadAcquiredCharacter
            {
                AccountId = slot.AccountKey.Value,
                CharacterKey = slot.CharacterKey.Value,
                ContentId = slot.ContentId,
            },
            AssignedSlotId = slot.SlotId,
            DesiredCharacterKey = slot.CharacterKey.Value,
            State = state,
            IsAvailable = false,
            IsEligibleForRun = false,
            StatusText = blocker,
        };

    private static DadParticipantSnapshot MarkWaiting(
        DadParticipantSnapshot participant,
        DadParticipantState state,
        string blocker)
    {
        participant.State = state;
        participant.StatusText = blocker;
        return participant;
    }

    private static bool TryBuildModulePayload(
        DadRunRequest request,
        DadPlannedModuleExecution module,
        out DadFrozenModulePayload payload,
        out string blocker)
    {
        payload = new DadFrozenModulePayload
        {
            ModuleId = module.ModuleId,
            ExpectedPartySize = module.ExpectedPartySize,
        };
        blocker = string.Empty;
        var expectedPartySize = 1;

        switch (module.ModuleId)
        {
            case DadModuleId.Duty when request.Dungeon != null:
                payload.DutyName = request.Dungeon.SelectedDungeon;
                payload.ContentFinderConditionId = request.Dungeon.ContentFinderConditionId;
                payload.Unsynced = request.Dungeon.Unsynced;
                expectedPartySize = request.Dungeon.QueueViaLanParty ? 4 : 1;
                break;
            case DadModuleId.Msq when request.Msq != null:
                payload.DutyName = request.Msq.DutyName;
                payload.ContentFinderConditionId = request.Msq.ContentFinderConditionId;
                break;
            case DadModuleId.DutySupport when request.DutySupport != null:
                payload.DutyName = request.DutySupport.DutyName;
                payload.ContentFinderConditionId = request.DutySupport.ContentFinderConditionId;
                break;
            case DadModuleId.Trust when request.Trust != null:
                payload.DutyName = request.Trust.DutyName;
                payload.ContentFinderConditionId = request.Trust.ContentFinderConditionId;
                break;
            case DadModuleId.PremadeDuty when request.PremadeDuty != null:
                payload.DutyName = request.PremadeDuty.DutyName;
                payload.ContentFinderConditionId = request.PremadeDuty.ContentFinderConditionId;
                payload.Unsynced = request.PremadeDuty.Unsynced;
                expectedPartySize = request.PremadeDuty.ExpectedPartySize;
                break;
            case DadModuleId.DailyMsq when request.DailyMsq != null:
                ApplyQueueTarget(payload, request.DailyMsq.QueueTarget);
                expectedPartySize = 4;
                break;
            case DadModuleId.Blunderville when request.Blunderville != null:
                payload.DutyName = request.Blunderville.Mode;
                break;
            case DadModuleId.Mogtome when request.Mogtome != null:
                payload.DutyName = request.Mogtome.Preset;
                break;
            case DadModuleId.Commendation when request.Commendation != null:
                payload.DutyName = request.Commendation.DutyName;
                payload.ContentFinderConditionId = request.Commendation.ContentFinderConditionId;
                if (payload.ContentFinderConditionId == 0)
                    ApplyQueueTarget(payload, request.Commendation.QueueTarget);
                expectedPartySize = 4;
                break;
            case DadModuleId.Astrope when request.Astrope != null:
                ApplyQueueTarget(payload, request.Astrope.QueueTarget);
                expectedPartySize = 4;
                break;
            case DadModuleId.CustomDuty when request.CustomDuty != null:
                payload.DutyName = request.CustomDuty.DutyName;
                payload.ContentFinderConditionId = request.CustomDuty.ContentFinderConditionId;
                payload.Unsynced = request.CustomDuty.Unsynced;
                expectedPartySize = request.CustomDuty.ExpectedPartySize;
                break;
            case DadModuleId.Squadron when request.Squadron != null:
                payload.DutyName = request.Squadron.DutyName;
                payload.ContentFinderConditionId = request.Squadron.ContentFinderConditionId;
                break;
            case DadModuleId.VariantVvd when request.VariantVvd != null:
                payload.DutyName = request.VariantVvd.DutyName;
                payload.ContentFinderConditionId = request.VariantVvd.ContentFinderConditionId;
                payload.Unsynced = request.VariantVvd.Unsynced;
                expectedPartySize = request.VariantVvd.ExpectedPartySize;
                break;
            default:
                blocker = $"Module '{module.ModuleId}' does not have one matching request payload.";
                return false;
        }

        if (expectedPartySize != module.ExpectedPartySize)
        {
            blocker = $"{module.DisplayName} payload party size {expectedPartySize} contradicts planned size {module.ExpectedPartySize}.";
            return false;
        }

        return true;
    }

    private static void ApplyQueueTarget(DadFrozenModulePayload payload, DadQueueTarget target)
    {
        payload.DutyName = target.DisplayName;
        payload.TargetKind = target.Kind;
        payload.ContentFinderConditionId = target.ContentFinderConditionId;
        payload.RouletteId = target.RouletteId;
    }

    private static bool SameOrderedRoster(
        IReadOnlyList<DadRosterCharacterRef> left,
        IReadOnlyList<DadRosterCharacterRef> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (!Same(left[index].AccountKey.Value, right[index].AccountKey.Value) ||
                !Same(left[index].CharacterKey.Value, right[index].CharacterKey.Value) ||
                left[index].ContentId != right[index].ContentId ||
                left[index].RequiredJobId != right[index].RequiredJobId ||
                left[index].AdsLootMode != right[index].AdsLootMode)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    private static bool Fail(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }
}
