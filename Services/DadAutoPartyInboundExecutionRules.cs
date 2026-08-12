using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

internal sealed record DadAutoPartyInboundExecutionContext(
    EndpointExecutionPlan ExecutionPlan,
    DadNativePartyInviteTarget Target,
    string SenderIslandId,
    string OwnerId,
    DateTimeOffset ExpiresAt,
    DadExpectedPartyInviter? FrozenInviter = null,
    IReadOnlyList<DadNativePartyInviteTarget>? PartyInviteTargets = null);

internal static class DadAutoPartyInboundExecutionRules
{
    public static bool TryBuildWorkerCommand(
        ExecutionOperation operation,
        DadAutoPartyInboundExecutionContext context,
        DadParticipantSnapshot liveParticipant,
        out DadWorkerExecutionCommand command,
        out DadParticipantSnapshot commandParticipant,
        out string blocker)
    {
        command = new DadWorkerExecutionCommand();
        commandParticipant = new DadParticipantSnapshot();
        blocker = string.Empty;
        var plan = context.ExecutionPlan;
        var target = context.Target;
        var reference = operation.ModuleReference;
        if (operation.Kind is not ExecutionOperationKind.Queue and not ExecutionOperationKind.Settle ||
            reference == null || plan.FormationOnly ||
            reference.ModuleIndex < 0 || reference.ModuleIndex >= plan.Modules.Length ||
            plan.Modules[reference.ModuleIndex] is not { } selectedModule ||
            selectedModule.ModuleIndex != reference.ModuleIndex ||
            !string.Equals(selectedModule.ModuleId, reference.ModuleId, StringComparison.Ordinal) ||
            !string.Equals(plan.RunId, target.RunId, StringComparison.Ordinal) ||
            context.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Fail("dad-inbound-queue-module-reference-invalid", out blocker);
        }

        var ordered = plan.Participants
            .OrderBy(static participant => DadPlannerSlotRules.GetSlotSortKey(participant.SlotId))
            .ToList();
        if (ordered.Count is < 1 or > 8 ||
            ordered.Select(static participant => participant.SlotId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Count)
        {
            return Fail("dad-inbound-queue-roster-invalid", out blocker);
        }

        var localProtocolRows = ordered.Where(participant =>
                string.Equals(participant.SlotId, target.SlotId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(participant.CharacterId.Value, operation.CharacterId.Value, StringComparison.Ordinal))
            .ToList();
        if (localProtocolRows.Count != 1 || !MatchesTarget(liveParticipant, target))
            return Fail("dad-inbound-queue-worker-route-mismatch", out blocker);

        var roster = new List<DadRosterCharacterRef>(ordered.Count);
        var participantRows = new List<DadParticipantSnapshot>(ordered.Count);
        var leaderCharacterKey = string.Empty;
        foreach (var participant in ordered)
        {
            if (!uint.TryParse(
                    participant.RequestedJob.Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var requestedJobId) ||
                !DadRosterCharacterMerge.IsCombatJob(requestedJobId))
            {
                return Fail("dad-inbound-queue-requested-job-invalid", out blocker);
            }

            var isLocal = ReferenceEquals(participant, localProtocolRows[0]);
            var accountKey = isLocal
                ? target.AccountKey
                : new DadAccountKey($"autoparty-account-{participant.SlotId.ToLowerInvariant()}");
            var characterKey = isLocal
                ? target.CharacterKey
                : new DadCharacterKey($"autoparty-character-{participant.SlotId.ToLowerInvariant()}");
            var contentId = isLocal
                ? target.ContentId
                : ulong.MaxValue - (ulong)DadPlannerSlotRules.GetSlotSortKey(participant.SlotId);
            var adsLootMode = ParseAdsLootMode(participant.AdsLootMode);
            roster.Add(new DadRosterCharacterRef
            {
                AccountKey = accountKey,
                CharacterKey = characterKey,
                ContentId = contentId,
                RequiredJobId = requestedJobId,
                AdsLootMode = adsLootMode,
            });

            DadParticipantSnapshot row;
            if (isLocal)
            {
                row = liveParticipant.Clone();
                row.RunId = plan.RunId;
                row.AssignedSlotId = participant.SlotId;
                row.DesiredCharacterKey = characterKey.Value;
                row.IsLocalClient = true;
                row.IsAuthority = participant.Role == EndpointExecutionRole.QueueLeader;
                commandParticipant = row;
            }
            else
            {
                row = new DadParticipantSnapshot
                {
                    WorkerSessionId = new DadWorkerSessionId(
                        $"autoparty-external-{operation.ProposalId:N}-{participant.SlotId.ToLowerInvariant()}"),
                    RunId = plan.RunId,
                    State = DadParticipantState.Ready,
                    ClaimState = DadClaimState.Granted,
                    LeaseState = DadParticipantLeaseState.Granted,
                    IsAvailable = true,
                    IsEligibleForRun = true,
                    PostArReady = true,
                    WorldReadyStable = true,
                    ManagedAccountKey = accountKey,
                    ActiveCharacterKey = characterKey,
                    AvailableCharacterKeys = [characterKey],
                    AssignedSlotId = participant.SlotId,
                    DesiredCharacterKey = characterKey.Value,
                    Character = new DadAcquiredCharacter
                    {
                        AccountId = accountKey.Value,
                        CharacterKey = characterKey.Value,
                        ContentId = contentId,
                        CurrentJobId = requestedJobId,
                        Readiness = DadReadinessState.Ready,
                        Freshness = DadSnapshotFreshness.Live,
                    },
                };
            }
            participantRows.Add(row);
            if (participant.Role == EndpointExecutionRole.QueueLeader)
                leaderCharacterKey = characterKey.Value;
        }

        if (string.IsNullOrWhiteSpace(leaderCharacterKey) ||
            ordered.Count(static participant => participant.Role == EndpointExecutionRole.QueueLeader) != 1)
            return Fail("dad-inbound-queue-authority-invalid", out blocker);

        if (!TryBuildPlan(operation, plan, roster, leaderCharacterKey, out var workerPlan, out blocker))
            return false;
        command = new DadWorkerExecutionCommand
        {
            SchemaVersion = DadWorkerCommandSchemaRules.ResolveEmissionSchema(workerPlan.Request.PreDutyRepairPolicy),
            CommandId = $"{operation.ProposalId:N}:{reference.ModuleIndex}:{target.SlotId}:autoparty-worker-execution",
            RunId = plan.RunId,
            ModuleIndex = reference.ModuleIndex,
            Role = localProtocolRows[0].Role == EndpointExecutionRole.QueueLeader
                ? DadWorkerExecutionRole.QueueLeader
                : DadWorkerExecutionRole.Participant,
            Plan = workerPlan,
            Participants = participantRows,
            TimeoutSeconds = Math.Max(60, plan.ParticipantReadyTimeoutSeconds + plan.AssemblyTimeoutSeconds + 900),
        };
        if (!DadWorkerCommandValidationRules.TryValidate(
                command,
                liveParticipant,
                out _,
                out var validationBlocker))
        {
            return Fail($"dad-inbound-queue-command-invalid:{validationBlocker}", out blocker);
        }
        return true;
    }

    private static bool TryBuildPlan(
        ExecutionOperation operation,
        EndpointExecutionPlan endpointPlan,
        List<DadRosterCharacterRef> roster,
        string leaderCharacterKey,
        out DadRunPlan plan,
        out string blocker)
    {
        plan = new DadRunPlan();
        blocker = string.Empty;
        var orchestration = new DadOrchestrationIntent
        {
            AuthorityMode = DadAuthorityMode.ServerDad,
            ModuleTarget = endpointPlan.Modules.Length > 1 ? DadModuleId.Mixed : DadModuleId.None,
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            TransportMode = roster.Count > 1 ? DadTransportMode.ServerHub : DadTransportMode.LocalOnly,
            RequirePostArReady = endpointPlan.RequirePostArReady,
            PreferredLeaderCharacterKey = new DadCharacterKey(leaderCharacterKey),
            PreferredInviterCharacterKey = new DadCharacterKey(leaderCharacterKey),
            RequiredAccountKeys = roster.Select(static row => row.AccountKey).ToList(),
            RequiredCharacterKeys = roster.Select(static row => row.CharacterKey).ToList(),
            RequiredRosterCharacters = roster.Select(CloneRosterReference).ToList(),
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = roster.Count,
                RequireRemoteParticipants = roster.Count > 1,
                RequireExactCharacters = true,
            },
            WaitPolicy = new DadRunWaitPolicy
            {
                ParticipantReadyTimeoutSeconds = endpointPlan.ParticipantReadyTimeoutSeconds,
                AssemblyTimeoutSeconds = endpointPlan.AssemblyTimeoutSeconds,
                LeaseDurationSeconds = endpointPlan.LeaseDurationSeconds,
            },
            AutoPartyProposalId = operation.ProposalId.ToString("D"),
        };
        var repairPolicy = new DadPreDutyRepairPolicy
        {
            Enabled = endpointPlan.RepairPolicy.Enabled,
            ThresholdPercent = endpointPlan.RepairPolicy.ThresholdPercent,
            Mode = endpointPlan.RepairPolicy.Mode switch
            {
                "npc-no-inn" => DadPreDutyRepairMode.NpcExcludingInns,
                "npc-no-teleport-no-inn" => DadPreDutyRepairMode.NearbyNpcNoTeleportOrInn,
                _ => DadPreDutyRepairMode.Self,
            },
        }.Normalize();
        var request = new DadRunRequest
        {
            RequestId = endpointPlan.RunId,
            RequestedBy = "autoparty",
            RequestedAtUtc = DateTime.UtcNow,
            Orchestration = orchestration,
            PreDutyRepairPolicy = repairPolicy,
        };
        var modules = new List<DadPlannedModuleExecution>(endpointPlan.Modules.Length);
        foreach (var endpointModule in endpointPlan.Modules.OrderBy(static module => module.ModuleIndex))
        {
            if (endpointModule.ModuleIndex != modules.Count ||
                !Enum.TryParse<DadModuleId>(endpointModule.ModuleId, ignoreCase: false, out var moduleId) ||
                moduleId is DadModuleId.None or DadModuleId.Mixed ||
                endpointModule.ExpectedPartySize is < 1 or > 8 ||
                !ApplyRequestModule(request, endpointModule, moduleId))
            {
                return Fail("dad-inbound-queue-plan-invalid", out blocker);
            }
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = moduleId,
                DisplayName = endpointModule.DisplayName,
                OwnerLabel = "AutoParty",
                ExpectedPartySize = endpointModule.ExpectedPartySize,
                RequiresPeers = endpointModule.ExpectedPartySize > 1,
                Summary = endpointModule.DisplayName,
            });
        }
        if (modules.Count == 0)
            return Fail("dad-inbound-queue-plan-invalid", out blocker);
        orchestration.ModuleTarget = modules.Count > 1 ? DadModuleId.Mixed : modules[0].ModuleId;
        plan = new DadRunPlan
        {
            Request = request,
            CompositeModuleId = orchestration.ModuleTarget,
            Orchestration = orchestration,
            Summary = string.Join(" | ", modules.Select(static module => module.Summary)),
            RequiredParticipantCount = roster.Count,
            RequiresRemoteParticipants = roster.Count > 1,
            LeaderCharacterKey = leaderCharacterKey,
            InviterCharacterKey = leaderCharacterKey,
            Modules = modules,
        };
        return DadRunSlotManifestRules.TryCreate(plan, out _, out blocker);
    }

    private static bool ApplyRequestModule(
        DadRunRequest request,
        EndpointExecutionModule module,
        DadModuleId moduleId)
    {
        var target = QueueTarget(module);
        switch (moduleId)
        {
            case DadModuleId.Duty:
                request.Dungeon = new DadDungeonTask
                {
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    SelectedDungeon = module.DisplayName,
                    QueueViaLanParty = module.ExpectedPartySize > 1,
                    Unsynced = module.Unsynced,
                };
                break;
            case DadModuleId.Msq:
                request.Msq = new DadMsqTask
                {
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                };
                break;
            case DadModuleId.DutySupport:
                request.DutySupport = new DadDutySupportTask
                {
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                };
                break;
            case DadModuleId.Trust:
                request.Trust = new DadTrustTask
                {
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                };
                break;
            case DadModuleId.PremadeDuty:
                request.PremadeDuty = new DadPremadeDutyTask
                {
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                    ExpectedPartySize = module.ExpectedPartySize,
                    Unsynced = module.Unsynced,
                };
                break;
            case DadModuleId.DailyMsq:
                request.DailyMsq = new DadDailyMsqTask { QueueTarget = target };
                break;
            case DadModuleId.Blunderville:
                request.Blunderville = new DadBlundervilleTask { Mode = module.DisplayName };
                break;
            case DadModuleId.Mogtome:
                request.Mogtome = new DadMogtomeTask { Preset = module.DisplayName };
                break;
            case DadModuleId.Commendation:
                request.Commendation = new DadCommendationTask
                {
                    QueueTarget = target,
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                };
                break;
            case DadModuleId.Astrope:
                request.Astrope = new DadAstropeTask { QueueTarget = target };
                break;
            case DadModuleId.CustomDuty:
                request.CustomDuty = new DadCustomDutyTask
                {
                    QueueTarget = target,
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                    ExpectedPartySize = module.ExpectedPartySize,
                    Unsynced = module.Unsynced,
                };
                break;
            case DadModuleId.Squadron:
                request.Squadron = new DadSquadronTask
                {
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                };
                break;
            case DadModuleId.VariantVvd:
                request.VariantVvd = new DadVariantVvdTask
                {
                    ContentFinderConditionId = module.ContentFinderConditionId,
                    DutyName = module.DisplayName,
                    ExpectedPartySize = module.ExpectedPartySize,
                    Unsynced = module.Unsynced,
                };
                break;
            default:
                return false;
        }
        return true;
    }

    private static DadQueueTarget QueueTarget(EndpointExecutionModule module)
        => new()
        {
            Kind = Enum.TryParse<DadQueueTargetKind>(module.TargetKind, ignoreCase: false, out var targetKind)
                ? targetKind
                : module.RouletteId > 0
                    ? DadQueueTargetKind.Roulette
                    : DadQueueTargetKind.DutyFinderDuty,
            ContentFinderConditionId = module.ContentFinderConditionId,
            RouletteId = module.RouletteId,
            DisplayName = module.DisplayName,
            Key = module.ActivityId.Value,
        };

    private static DadRosterCharacterRef CloneRosterReference(DadRosterCharacterRef source)
        => new()
        {
            AccountKey = source.AccountKey,
            CharacterKey = source.CharacterKey,
            ContentId = source.ContentId,
            RequiredJobId = source.RequiredJobId,
            AdsLootMode = source.AdsLootMode,
        };

    private static DadAdsLootMode ParseAdsLootMode(string? value)
        => value switch
        {
            "need" => DadAdsLootMode.Need,
            "greed" => DadAdsLootMode.Greed,
            "pass" => DadAdsLootMode.Pass,
            _ => DadAdsLootMode.NoChange,
        };

    private static bool MatchesTarget(DadParticipantSnapshot participant, DadNativePartyInviteTarget target)
        => string.Equals(
               participant.WorkerSessionId.Value,
               target.WorkerSessionId.Value,
               StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(participant.ManagedAccountKey, target.AccountKey) &&
           DadRosterIdentity.SameCharacter(
               participant.ActiveCharacterKey,
               participant.Character.ContentId,
               target.CharacterKey,
               target.ContentId) &&
           string.Equals(participant.AssignedSlotId, target.SlotId, StringComparison.OrdinalIgnoreCase);

    private static bool Fail(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }
}
