using dad.Models;

namespace dad.Services;

public sealed class DadPlannerService
{
    private readonly DadPresetProviderService presetProviderService;
    private readonly DadModuleRegistry moduleRegistry;
    private readonly Configuration configuration;

    public DadPlannerService(DadPresetProviderService presetProviderService, DadModuleRegistry moduleRegistry, Configuration configuration)
    {
        this.presetProviderService = presetProviderService;
        this.moduleRegistry = moduleRegistry;
        this.configuration = configuration;
    }

    public DadRunPlan? BuildPlan(
        DadRunRequest request,
        DadCharacterPool pool,
        out string rejectionReason,
        bool requireLiveReadiness = true,
        bool allowWakeableCoordinatorLeader = false,
        DadAcquiredCharacter? unfilteredLocalRuntimeCharacter = null)
    {
        rejectionReason = "No dad tasks were configured.";
        request.ApplyOrchestrationDefaults();

        if (!ResolveNpcAutoLevelSelections(request, pool, out rejectionReason))
            return null;

        var modules = new List<DadPlannedModuleExecution>();
        if (request.Dungeon != null)
        {
            if (request.Dungeon.Count <= 0)
            {
                rejectionReason = "dad dungeon count must be greater than 0.";
                return null;
            }

            if (request.Dungeon.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.Dungeon.SelectedDungeon))
            {
                rejectionReason = "dad dungeon requires a Duty Finder row id and duty name.";
                return null;
            }

            if (!DadRunRequestOptions.ValidFrequencies.Contains(request.Dungeon.Frequency))
            {
                rejectionReason = $"dad dungeon frequency '{request.Dungeon.Frequency}' is not supported.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Duty);
            var expectedPartySize = request.Dungeon.QueueViaLanParty ? 4 : 1;
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Duty,
                DisplayName = request.Dungeon.SelectedDungeon,
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = expectedPartySize,
                RequiresPeers = expectedPartySize > 1,
                Summary = $"{request.Dungeon.Count}x {request.Dungeon.SelectedDungeon} #{request.Dungeon.ContentFinderConditionId}",
            });
        }

        if (request.Msq != null)
        {
            if (request.Msq.Attempts <= 0)
            {
                rejectionReason = "dad MSQ attempts must be greater than 0.";
                return null;
            }
            if (request.Msq.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.Msq.DutyName))
            {
                rejectionReason = "dad MSQ solo progression requires a selected Duty Finder duty.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Msq);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Msq,
                DisplayName = "MSQ",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = 1,
                RequiresPeers = false,
                Summary = $"MSQ {request.Msq.DutyName} #{request.Msq.ContentFinderConditionId}; Trust then Duty Support fallback",
            });
        }

        if (request.DutySupport != null)
        {
            if (request.DutySupport.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.DutySupport.DutyName))
            {
                rejectionReason = "dad Duty Support requires a Duty Finder row id and duty name.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.DutySupport);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.DutySupport,
                DisplayName = "Duty Support",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = 1,
                RequiresPeers = false,
                Summary = $"Duty Support {request.DutySupport.DutyName} #{request.DutySupport.ContentFinderConditionId}",
            });
        }

        if (request.Trust != null)
        {
            if (request.Trust.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.Trust.DutyName))
            {
                rejectionReason = "dad Trust requires a Duty Finder row id and duty name.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Trust);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Trust,
                DisplayName = "Trust",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = 1,
                RequiresPeers = false,
                Summary = $"Trust {request.Trust.DutyName} #{request.Trust.ContentFinderConditionId}",
            });
        }

        if (request.PremadeDuty != null)
        {
            if (request.PremadeDuty.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.PremadeDuty.DutyName))
            {
                rejectionReason = "dad Premade Duty requires a Duty Finder row id and duty name.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.PremadeDuty);
            var expectedPartySize = Math.Max(2, request.PremadeDuty.ExpectedPartySize);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.PremadeDuty,
                DisplayName = "Premade Duty",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = expectedPartySize,
                RequiresPeers = expectedPartySize > 1,
                Summary = $"Premade {request.PremadeDuty.DutyName} #{request.PremadeDuty.ContentFinderConditionId}",
            });
        }

        if (request.DailyMsq != null)
        {
            var target = request.DailyMsq.QueueTarget ??= new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
            if (target.Kind != DadQueueTargetKind.Roulette)
            {
                rejectionReason = $"dad Daily Roulette requires a Roulette target kind, not {target.Kind}.";
                return null;
            }

            if (target.RouletteId == 0 &&
                string.Equals(target.Key, DadRouletteCatalogProjection.MainScenarioLegacyKey, StringComparison.OrdinalIgnoreCase))
            {
                target.RouletteId = DadRouletteCatalogProjection.MainScenarioRouletteId;
                target.Key = DadRouletteCatalogProjection.BuildCanonicalKey(target.RouletteId);
                if (string.IsNullOrWhiteSpace(target.DisplayName))
                    target.DisplayName = "Main Scenario";
            }

            if (target.RouletteId is 0 or > byte.MaxValue)
            {
                rejectionReason = "dad Daily Roulette requires a ContentRoulette id in the range 1..255.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.DailyMsq);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.DailyMsq,
                DisplayName = "Daily Roulette",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = Math.Max(4, capability.RequiredPartySize),
                RequiresPeers = true,
                Summary = $"Daily Roulette {target.DisplayName} #{target.RouletteId}",
            });
        }

        if (request.Blunderville != null)
        {
            if (request.Blunderville.Attempts <= 0)
            {
                rejectionReason = "dad Blunderville attempts must be greater than 0.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(request.Blunderville.EmoteCommand) ||
                !request.Blunderville.EmoteCommand.StartsWith("/", StringComparison.Ordinal) ||
                request.Blunderville.EmoteCommand.Contains('\n') ||
                request.Blunderville.EmoteCommand.Contains('\r'))
            {
                rejectionReason = "dad Blunderville requires a validated single-line per-character emote command.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Blunderville);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Blunderville,
                DisplayName = "Blunderville",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = 1,
                RequiresPeers = false,
                Summary = "Blunderville emote run",
            });
        }

        if (request.Mogtome != null)
        {
            if (request.Mogtome.Attempts <= 0)
            {
                rejectionReason = "dad MOGTOME attempts must be greater than 0.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Mogtome);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Mogtome,
                DisplayName = "MOGTOME",
                OwnerLabel = capability.OwnerLabel,
                // B3 (Option A, reversible): MOGTOME runs as a solo DAD-owned helper-IPC lane; the helper
                // coordinates its own party, so Dad must not gate it as a 4-peer premade or reject it under
                // local-only. The executor keeps its ipc.IsReady() check. Revert to Math.Max(4, ...) +
                // RequiresPeers = true to restore the legacy 4-person premade topology.
                ExpectedPartySize = Math.Max(1, capability.RequiredPartySize),
                RequiresPeers = false,
                Summary = $"MOGTOME preset '{request.Mogtome.Preset}'",
            });
        }

        if (request.Commendation != null)
        {
            if (request.Commendation.Attempts <= 0)
            {
                rejectionReason = "dad commendation attempts must be greater than 0.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Commendation);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Commendation,
                DisplayName = "Commendation",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = Math.Max(4, capability.RequiredPartySize),
                RequiresPeers = true,
                Summary = $"{request.Commendation.Attempts} commendation attempt(s)",
            });
        }

        if (request.Astrope != null)
        {
            if (request.Astrope.Attempts <= 0)
            {
                rejectionReason = "dad Astrope attempts must be greater than 0.";
                return null;
            }

            if (!TimeSpan.TryParse(request.Astrope.ValidLocalTimeWindow.StartLocal, out _) ||
                !TimeSpan.TryParse(request.Astrope.ValidLocalTimeWindow.EndLocal, out _))
            {
                rejectionReason = "dad Astrope local-time window is invalid.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Astrope);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Astrope,
                DisplayName = "Astrope",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = Math.Max(4, capability.RequiredPartySize),
                RequiresPeers = true,
                Summary = $"{request.Astrope.Attempts} Astrope attempt(s) in {request.Astrope.ValidLocalTimeWindow.Describe()}",
            });
        }

        if (request.CustomDuty != null)
        {
            if (request.CustomDuty.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.CustomDuty.DutyName))
            {
                rejectionReason = "dad Custom Duty requires a Duty Finder row id and duty name.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.CustomDuty);
            var expectedPartySize = Math.Clamp(request.CustomDuty.ExpectedPartySize, 1, 8);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.CustomDuty,
                DisplayName = "Custom Duty",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = expectedPartySize,
                RequiresPeers = expectedPartySize > 1,
                Summary = $"Custom duty {request.CustomDuty.DutyName} #{request.CustomDuty.ContentFinderConditionId}",
            });
        }

        if (request.Squadron != null)
        {
            if (request.Squadron.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.Squadron.DutyName))
            {
                rejectionReason = "dad Squadron requires a Duty Finder row id and duty name.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.Squadron);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Squadron,
                DisplayName = "Squadron",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = 1,
                RequiresPeers = false,
                Summary = $"Squadron {request.Squadron.DutyName} #{request.Squadron.ContentFinderConditionId}",
            });
        }

        if (request.VariantVvd != null)
        {
            if (request.VariantVvd.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(request.VariantVvd.DutyName))
            {
                rejectionReason = "dad Variant/VVD requires a Duty Finder row id and duty name.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.VariantVvd);
            var expectedPartySize = Math.Clamp(request.VariantVvd.ExpectedPartySize, 1, 4);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.VariantVvd,
                DisplayName = "Variant / VVD",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = expectedPartySize,
                RequiresPeers = expectedPartySize > 1,
                Summary = $"Variant/VVD {request.VariantVvd.DutyName} #{request.VariantVvd.ContentFinderConditionId}",
            });
        }

        if (modules.Count == 0)
            return null;

        if (requireLiveReadiness && !ValidateLocalNpcDutyRunner(request, pool, out rejectionReason))
            return null;

        if (!ValidateStopPolicyAtStart(request, pool, out rejectionReason))
            return null;

        if (request.Orchestration.LocalOnlyOverride && modules.Any(static module => module.RequiresPeers))
        {
            rejectionReason = request.Orchestration.ModuleTarget switch
            {
                // B4: MSQ arm removed — MSQ modules are always built RequiresPeers = false, so this guard is
                // never entered for an MSQ-only run; the arm was unreachable copy-paste drift.
                DadModuleId.PremadeDuty => "dad local-only is enabled, but Premade Duty requires Dad Coordinator party workers.",
                DadModuleId.DailyMsq => "dad local-only is enabled, but Daily Roulette requires Dad Coordinator party workers.",
                // B3 (Option A): MOGTOME arm removed — MOGTOME now plans RequiresPeers = false (solo helper-IPC
                // lane), so it is no longer rejected under local-only.
                DadModuleId.Commendation => "dad local-only is enabled, but commendation requires Dad Coordinator party workers.",
                DadModuleId.Astrope => "dad local-only is enabled, but Astrope requires Dad Coordinator party workers.",
                DadModuleId.VariantVvd => "dad local-only is enabled, but Variant/VVD party mode requires Dad Coordinator party workers.",
                _ => "dad local-only is enabled, but this run requires peer workers.",
            };
            return null;
        }

        if (requireLiveReadiness &&
            !ValidateRequiredRuntimeParticipants(request, pool, configuration.PartyValidationOverrideEnabled, out rejectionReason))
            return null;

        var activeCoordinatorCharacter = DadFullPartyExecutionRules.ResolveActiveCoordinatorCharacter(
            unfilteredLocalRuntimeCharacter,
            pool.Characters);
        var localCharacterKey = activeCoordinatorCharacter?.CharacterKey ?? string.Empty;
        var leaderCharacterKey = string.IsNullOrWhiteSpace(request.Orchestration.PreferredLeaderCharacterKey)
            ? localCharacterKey
            : request.Orchestration.PreferredLeaderCharacterKey.Value;
        var requiredParticipantCount = Math.Max(
            request.Orchestration.RosterIntent.ExpectedPartySize,
            modules.Max(static module => module.ExpectedPartySize));
        var inviterCharacterKey = ResolveInviterCharacterKey(
            request,
            leaderCharacterKey,
            localCharacterKey,
            activeCoordinatorCharacter);

        if (!ValidatePartyAuthority(
                request,
                pool,
                requiredParticipantCount,
                leaderCharacterKey,
                inviterCharacterKey,
                requireLiveReadiness,
                allowWakeableCoordinatorLeader,
                activeCoordinatorCharacter,
                out rejectionReason))
            return null;

        return new DadRunPlan
        {
            Request = request,
            CompositeModuleId = modules.Count > 1 ? DadModuleId.Mixed : modules[0].ModuleId,
            Orchestration = request.Orchestration,
            Summary = string.Join(" | ", modules.Select(static module => module.Summary)),
            RequiredParticipantCount = requiredParticipantCount,
            RequiresRemoteParticipants = !request.Orchestration.LocalOnlyOverride && modules.Any(static module => module.RequiresPeers),
            LeaderCharacterKey = leaderCharacterKey,
            InviterCharacterKey = inviterCharacterKey,
            Modules = modules,
            PlannerWarnings = DadPlannerWarningRules.Build(request, pool),
        };
    }

    private static string ResolveInviterCharacterKey(
        DadRunRequest request,
        string leaderCharacterKey,
        string localCharacterKey,
        DadAcquiredCharacter? activeCoordinatorCharacter)
        => request.Orchestration.InviteAuthority switch
        {
            DadInviteAuthority.NotNeeded => string.Empty,
            DadInviteAuthority.ServerDad when DadFullPartyExecutionRules.RequiresLocalCoordinatorLeader(request) => leaderCharacterKey,
            DadInviteAuthority.ServerDad => activeCoordinatorCharacter?.CharacterKey ?? localCharacterKey,
            DadInviteAuthority.PresetLeader => request.Orchestration.PreferredInviterCharacterKey.IsEmpty
                ? leaderCharacterKey
                : request.Orchestration.PreferredInviterCharacterKey.Value,
            _ => request.Orchestration.PreferredInviterCharacterKey.Value ?? string.Empty,
        };

    private bool ValidatePartyAuthority(
        DadRunRequest request,
        DadCharacterPool pool,
        int requiredParticipantCount,
        string leaderCharacterKey,
        string inviterCharacterKey,
        bool requireLiveReadiness,
        bool allowWakeableCoordinatorLeader,
        DadAcquiredCharacter? activeCoordinatorCharacter,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (requiredParticipantCount <= 1 || request.Orchestration.LocalOnlyOverride)
            return true;

        if (request.Orchestration.QueueAuthority != DadQueueAuthority.Leader)
        {
            rejectionReason = $"Party queue authority must be the preset leader; request has {request.Orchestration.QueueAuthority}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(leaderCharacterKey))
        {
            rejectionReason = "Party leader is not selected.";
            return false;
        }

        var leader = ResolveAuthorityCharacter(pool, leaderCharacterKey, activeCoordinatorCharacter);
        if (leader == null)
        {
            rejectionReason = $"Party leader '{leaderCharacterKey}' is not known to Dad.";
            return false;
        }

        var coordinatorAccountKey = DadSchedulerRoutingRules.ResolveStableClientAccount(configuration.ClientAccountId);
        if (!DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
                request,
                leader,
                coordinatorAccountKey,
                activeCoordinatorCharacter,
                requireExactLocalIdentity: requireLiveReadiness,
                allowWakeableCoordinatorLeader: !requireLiveReadiness && allowWakeableCoordinatorLeader,
                out rejectionReason))
            return false;

        if (requireLiveReadiness && !IsConnectedForRuntime(leader))
        {
            rejectionReason = $"Party leader '{leaderCharacterKey}' is not live/ready at runtime.";
            return false;
        }

        if (requireLiveReadiness && leader.Blockers.Any(IsLocalIsolationReason))
        {
            rejectionReason = $"Party leader '{leaderCharacterKey}' is local-only/isolated and cannot queue the Dad party.";
            return false;
        }

        if (request.Orchestration.InviteAuthority == DadInviteAuthority.External)
        {
            rejectionReason = "External party inviter is not executable by Dad; select Preset leader or Dad Coordinator.";
            return false;
        }

        if (request.Orchestration.InviteAuthority == DadInviteAuthority.NotNeeded)
        {
            rejectionReason = "Party invite authority is marked Not needed, but this run requires a party.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(inviterCharacterKey))
        {
            rejectionReason = "Party inviter is not selected.";
            return false;
        }

        var inviter = ResolveAuthorityCharacter(pool, inviterCharacterKey, activeCoordinatorCharacter);
        if (inviter == null)
        {
            rejectionReason = $"Party inviter '{inviterCharacterKey}' is not known to Dad.";
            return false;
        }

        if (requireLiveReadiness && !IsConnectedForRuntime(inviter))
        {
            rejectionReason = $"Party inviter '{inviterCharacterKey}' is not live/ready at runtime.";
            return false;
        }

        if (request.Orchestration.InviteAuthority == DadInviteAuthority.ServerDad)
        {
            if (requireLiveReadiness && inviter.Source != DadCharacterSource.LocalRuntime)
            {
                rejectionReason = $"Dad Coordinator inviter '{inviterCharacterKey}' is not loaded on this Dad client.";
                return false;
            }

            if (!IsPlannedPartyCharacter(request, inviterCharacterKey))
            {
                rejectionReason = $"Dad Coordinator inviter '{inviterCharacterKey}' is not one of the planned party characters.";
                return false;
            }
        }

        return true;
    }

    private static DadAcquiredCharacter? ResolveAuthorityCharacter(
        DadCharacterPool pool,
        string characterKey,
        DadAcquiredCharacter? activeCoordinatorCharacter)
    {
        if (activeCoordinatorCharacter != null &&
            string.Equals(activeCoordinatorCharacter.CharacterKey, characterKey, StringComparison.OrdinalIgnoreCase))
        {
            return activeCoordinatorCharacter;
        }

        return pool.Characters.FirstOrDefault(character =>
            string.Equals(character.CharacterKey, characterKey, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValidateRequiredRuntimeParticipants(DadRunRequest request, DadCharacterPool pool, bool partyValidationOverride, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        var requiredAccounts = request.Orchestration.RequiredAccountKeys
            .Select(static key => key.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var requiredCharacters = request.Orchestration.RequiredCharacterKeys
            .Select(static key => key.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var requiredRosterCharacters = request.Orchestration.RequiredRosterCharacters
            .Where(static reference => reference is { IsEmpty: false })
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateAccount = requiredAccounts
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateAccount))
        {
            rejectionReason = $"Required account '{duplicateAccount}' appears in multiple planned slots.";
            return false;
        }

        var duplicateCharacter = requiredRosterCharacters.Count == 0
            ? requiredCharacters
                .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1)
                ?.Key
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(duplicateCharacter))
        {
            rejectionReason = $"Required character '{duplicateCharacter}' appears in multiple planned slots.";
            return false;
        }

        // Feature batch A: party-validation override skips runtime connectivity/readiness checks below.
        // Duplicate-slot checks above are correctness (prevent double-claims) and stay enforced. Default off.
        if (partyValidationOverride)
            return true;

        foreach (var account in requiredAccounts)
        {
            var matchingCharacters = pool.Characters
                .Where(character => MatchesAccountKey(character, account))
                .ToList();
            var liveCharacter = matchingCharacters.FirstOrDefault(IsConnectedForRuntime);
            if (liveCharacter == null)
            {
                rejectionReason = matchingCharacters.Any(static character => character.Source == DadCharacterSource.XadbOnly)
                    ? $"Required account '{account}' is only available from XADB/offline planner data and is not connected at runtime."
                    : $"Required account '{account}' is not connected to Dad at runtime.";
                return false;
            }

            if (liveCharacter.Blockers.Any(IsLocalIsolationReason))
            {
                rejectionReason = $"Required account '{account}' is connected but local-only/isolated and cannot accept remote Dad work.";
                return false;
            }
        }

        if (requiredRosterCharacters.Count > 0)
        {
            foreach (var reference in requiredRosterCharacters)
            {
                if (reference.CharacterKey.IsEmpty)
                    continue;

                var character = pool.Characters.FirstOrDefault(candidate => MatchesRosterReference(candidate, reference));
                if (character == null)
                {
                    rejectionReason = $"Required roster character '{reference.CharacterKey}' for account '{reference.AccountKey}' is not known to Dad.";
                    return false;
                }

                if (!IsConnectedForRuntime(character))
                {
                    var requiredAccount = ResolveAccountKey(character);
                    var activeOnSameAccount = string.IsNullOrWhiteSpace(requiredAccount)
                        ? null
                        : pool.Characters.FirstOrDefault(candidate =>
                            !string.Equals(candidate.CharacterKey, character.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                            MatchesAccountKey(candidate, requiredAccount) &&
                            IsConnectedForRuntime(candidate));
                    rejectionReason = activeOnSameAccount == null
                        ? $"Required character '{character.CharacterKey}' on account '{requiredAccount}' is not live/ready at runtime."
                        : $"Required account '{requiredAccount}' is connected as '{activeOnSameAccount.CharacterKey}', not required character '{character.CharacterKey}'.";
                    return false;
                }

                if (character.Blockers.Any(IsLocalIsolationReason))
                {
                    rejectionReason = $"Required character '{character.CharacterKey}' is local-only/isolated and cannot accept remote Dad work.";
                    return false;
                }
            }

            return true;
        }

        foreach (var characterKey in requiredCharacters)
        {
            var character = pool.Characters.FirstOrDefault(candidate =>
                string.Equals(candidate.CharacterKey, characterKey, StringComparison.OrdinalIgnoreCase));
            if (character == null)
            {
                rejectionReason = $"Required character '{characterKey}' is not known to Dad.";
                return false;
            }

            if (!IsConnectedForRuntime(character))
            {
                var requiredAccount = ResolveAccountKey(character);
                var activeOnSameAccount = string.IsNullOrWhiteSpace(requiredAccount)
                    ? null
                    : pool.Characters.FirstOrDefault(candidate =>
                        !string.Equals(candidate.CharacterKey, characterKey, StringComparison.OrdinalIgnoreCase) &&
                        MatchesAccountKey(candidate, requiredAccount) &&
                        IsConnectedForRuntime(candidate));
                rejectionReason = activeOnSameAccount == null
                    ? $"Required character '{characterKey}' is not live/ready at runtime."
                    : $"Required account '{requiredAccount}' is connected as '{activeOnSameAccount.CharacterKey}', not required character '{characterKey}'.";
                return false;
            }

            if (character.Blockers.Any(IsLocalIsolationReason))
            {
                rejectionReason = $"Required character '{characterKey}' is local-only/isolated and cannot accept remote Dad work.";
                return false;
            }
        }

        return true;
    }

    private bool ResolveNpcAutoLevelSelections(DadRunRequest request, DadCharacterPool pool, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        var localCharacter = pool.Characters.FirstOrDefault(static character =>
            character.Source == DadCharacterSource.LocalRuntime &&
            character.IsLiveConnected);

        if (request.DutySupport is { AutoSelectHighestEligible: true } dutySupport &&
            (dutySupport.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(dutySupport.DutyName)))
        {
            var selected = presetProviderService.SelectHighestEligibleNpcDuty(
                localCharacter,
                DadNpcAutoLevelLane.DutySupport,
                out var blocker);
            if (selected == null)
            {
                rejectionReason = blocker;
                return false;
            }

            dutySupport.ContentFinderConditionId = selected.ContentFinderConditionId;
            dutySupport.DutyName = selected.DutyDisplayName;
        }

        if (request.Trust is { AutoSelectHighestEligible: true } trust &&
            (trust.ContentFinderConditionId == 0 || string.IsNullOrWhiteSpace(trust.DutyName)))
        {
            var selected = presetProviderService.SelectHighestEligibleNpcDuty(
                localCharacter,
                DadNpcAutoLevelLane.Trust,
                out var blocker);
            if (selected == null)
            {
                rejectionReason = blocker;
                return false;
            }

            trust.ContentFinderConditionId = selected.ContentFinderConditionId;
            trust.DutyName = selected.DutyDisplayName;
        }

        return true;
    }

    private bool ValidateLocalNpcDutyRunner(
        DadRunRequest request,
        DadCharacterPool pool,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        var localNpcDuties = new List<(uint ContentFinderConditionId, string DutyName)>();
        if (request.DutySupport != null)
            localNpcDuties.Add((request.DutySupport.ContentFinderConditionId, request.DutySupport.DutyName));
        if (request.Trust != null)
            localNpcDuties.Add((request.Trust.ContentFinderConditionId, request.Trust.DutyName));
        if (localNpcDuties.Count == 0)
            return true;

        var localCharacter = pool.Characters.FirstOrDefault(static character =>
            character.Source == DadCharacterSource.LocalRuntime &&
            character.IsLiveConnected);
        if (localCharacter == null)
        {
            rejectionReason = "Duty Support/Trust requires a ready logged-in local character.";
            return false;
        }

        foreach (var dutyRequest in localNpcDuties)
        {
            var duty = presetProviderService.GetPlannerDuty(dutyRequest.ContentFinderConditionId);
            var blocker = DadNpcDutyEligibility.GetBlocker(
                localCharacter,
                string.IsNullOrWhiteSpace(dutyRequest.DutyName)
                    ? duty?.DutyDisplayName ?? string.Empty
                    : dutyRequest.DutyName,
                dutyRequest.ContentFinderConditionId,
                duty?.JobLevelRequired ?? 0);
            if (string.IsNullOrWhiteSpace(blocker))
                continue;

            rejectionReason = blocker;
            return false;
        }

        return true;
    }

    private static bool ValidateStopPolicyAtStart(DadRunRequest request, DadCharacterPool pool, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        request.StopPolicy ??= new DadRunStopPolicy();
        var policy = request.StopPolicy.Normalize();
        if (policy.Mode != DadPlannerStopMode.TargetLevel)
            return true;

        var targetKey = policy.TargetCharacterKey.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            rejectionReason = "Target-level stop requires an exact target character.";
            return false;
        }

        if (!IsStopTargetInPlannedRoster(request, targetKey))
        {
            rejectionReason = $"Target-level stop character '{targetKey}' is not one of the selected planner characters.";
            return false;
        }

        var character = pool.Characters.FirstOrDefault(candidate =>
            string.Equals(candidate.CharacterKey, targetKey, StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            rejectionReason = $"Target-level stop character '{targetKey}' is not known to Dad.";
            return false;
        }

        var currentLevel = DadRosterCharacterMerge.ResolveCurrentLevel(
            character.JobLevels,
            character.CurrentJobId,
            character.CurrentLevel);
        if (!currentLevel.HasValue)
        {
            rejectionReason = $"Target-level stop character '{targetKey}' has no current level data.";
            return false;
        }

        if (currentLevel.Value >= policy.TargetLevel)
        {
            rejectionReason = $"Target-level stop character '{targetKey}' is already level {currentLevel.Value}/{policy.TargetLevel}.";
            return false;
        }

        return true;
    }

    private static bool IsStopTargetInPlannedRoster(DadRunRequest request, string targetKey)
    {
        if (!request.Orchestration.PreferredLeaderCharacterKey.IsEmpty &&
            string.Equals(request.Orchestration.PreferredLeaderCharacterKey.Value, targetKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return request.Orchestration.RequiredCharacterKeys
                   .Any(key => string.Equals(key.Value, targetKey, StringComparison.OrdinalIgnoreCase))
               || request.Orchestration.PreferredCharacterKeys
                   .Any(key => string.Equals(key.Value, targetKey, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlannedPartyCharacter(DadRunRequest request, string characterKey)
        => IsStopTargetInPlannedRoster(request, characterKey)
           || request.Orchestration.RequiredRosterCharacters.Any(reference =>
               !reference.CharacterKey.IsEmpty &&
               string.Equals(reference.CharacterKey.Value, characterKey, StringComparison.OrdinalIgnoreCase))
           || request.Orchestration.PreferredRosterCharacters.Any(reference =>
               !reference.CharacterKey.IsEmpty &&
               string.Equals(reference.CharacterKey.Value, characterKey, StringComparison.OrdinalIgnoreCase));

    private static bool IsConnectedForRuntime(DadAcquiredCharacter character)
        => character.Source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime
           && character.Freshness is DadSnapshotFreshness.Live or DadSnapshotFreshness.Recent
           && character.Readiness == DadReadinessState.Ready;

    private static bool MatchesAccountKey(DadAcquiredCharacter character, string accountKey)
        => (!string.IsNullOrWhiteSpace(character.AccountId)
            && string.Equals(character.AccountId, accountKey, StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(character.AccountAlias)
               && string.Equals(character.AccountAlias, accountKey, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesRosterReference(DadAcquiredCharacter character, DadRosterCharacterRef reference)
    {
        if (!reference.AccountKey.IsEmpty && !MatchesAccountKey(character, reference.AccountKey.Value))
            return false;

        return DadRosterIdentity.SameCharacter(
            new DadCharacterKey(character.CharacterKey),
            character.ContentId,
            reference.CharacterKey,
            reference.ContentId);
    }

    private static string ResolveAccountKey(DadAcquiredCharacter character)
        => !string.IsNullOrWhiteSpace(character.AccountId)
            ? character.AccountId
            : character.AccountAlias;

    private static bool IsLocalIsolationReason(string blocker)
        => blocker.Contains("local-only", StringComparison.OrdinalIgnoreCase)
           || blocker.Contains("local only", StringComparison.OrdinalIgnoreCase)
           || blocker.Contains("isolated", StringComparison.OrdinalIgnoreCase);

}
