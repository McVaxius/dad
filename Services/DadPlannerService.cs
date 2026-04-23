using dad.Models;

namespace dad.Services;

public sealed class DadPlannerService
{
    private readonly DadPresetProviderService presetProviderService;
    private readonly DadModuleRegistry moduleRegistry;

    public DadPlannerService(DadPresetProviderService presetProviderService, DadModuleRegistry moduleRegistry)
    {
        this.presetProviderService = presetProviderService;
        this.moduleRegistry = moduleRegistry;
    }

    public DadRunPlan? BuildPlan(DadRunRequest request, DadCharacterPool pool, out string rejectionReason)
    {
        rejectionReason = "No dad tasks were configured.";
        request.ApplyOrchestrationDefaults();

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

            var capability = moduleRegistry.GetCapability(DadModuleId.Msq);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.Msq,
                DisplayName = "MSQ",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = Math.Max(4, capability.RequiredPartySize),
                RequiresPeers = true,
                Summary = $"MSQ preset '{request.Msq.Preset}'",
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
            if (string.IsNullOrWhiteSpace(request.DailyMsq.LanPartyPreset))
            {
                rejectionReason = "dad Daily MSQ requires a preset.";
                return null;
            }

            var capability = moduleRegistry.GetCapability(DadModuleId.DailyMsq);
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.DailyMsq,
                DisplayName = "Daily MSQ",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = Math.Max(4, capability.RequiredPartySize),
                RequiresPeers = true,
                Summary = $"Daily MSQ preset '{request.DailyMsq.LanPartyPreset}'",
            });
        }

        if (request.Blunderville != null)
        {
            if (request.Blunderville.Attempts <= 0)
            {
                rejectionReason = "dad Blunderville attempts must be greater than 0.";
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
                Summary = $"Blunderville mode '{request.Blunderville.Mode}'",
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
                ExpectedPartySize = Math.Max(4, capability.RequiredPartySize),
                RequiresPeers = true,
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
            modules.Add(new DadPlannedModuleExecution
            {
                ModuleId = DadModuleId.CustomDuty,
                DisplayName = "Custom Duty",
                OwnerLabel = capability.OwnerLabel,
                ExpectedPartySize = 1,
                RequiresPeers = false,
                Summary = $"Custom duty {request.CustomDuty.DutyName} #{request.CustomDuty.ContentFinderConditionId}",
            });
        }

        if (modules.Count == 0)
            return null;

        if (request.Orchestration.LocalOnlyOverride && modules.Any(static module => module.RequiresPeers))
        {
            rejectionReason = request.Orchestration.ModuleTarget switch
            {
                DadModuleId.Msq => "dad local-only is enabled, but MSQ requires Server Dad party workers.",
                DadModuleId.PremadeDuty => "dad local-only is enabled, but Premade Duty requires Server Dad party workers.",
                DadModuleId.DailyMsq => "dad local-only is enabled, but Daily MSQ requires Server Dad party workers.",
                DadModuleId.Mogtome => "dad local-only is enabled, but MOGTOME requires Server Dad party workers.",
                DadModuleId.Commendation => "dad local-only is enabled, but commendation requires Server Dad party workers.",
                DadModuleId.Astrope => "dad local-only is enabled, but Astrope requires Server Dad party workers.",
                _ => "dad local-only is enabled, but this run requires peer workers.",
            };
            return null;
        }

        var localCharacterKey = pool.Characters
            .FirstOrDefault(static character => character.Source == DadCharacterSource.LocalRuntime)
            ?.CharacterKey ?? string.Empty;

        return new DadRunPlan
        {
            Request = request,
            CompositeModuleId = modules.Count > 1 ? DadModuleId.Mixed : modules[0].ModuleId,
            Orchestration = request.Orchestration,
            Summary = string.Join(" | ", modules.Select(static module => module.Summary)),
            RequiredParticipantCount = Math.Max(request.Orchestration.RosterIntent.ExpectedPartySize, modules.Max(static module => module.ExpectedPartySize)),
            RequiresRemoteParticipants = !request.Orchestration.LocalOnlyOverride && modules.Any(static module => module.RequiresPeers),
            LeaderCharacterKey = string.IsNullOrWhiteSpace(request.Orchestration.PreferredLeaderCharacterKey)
                ? localCharacterKey
                : request.Orchestration.PreferredLeaderCharacterKey,
            Modules = modules,
            PlannerWarnings = BuildPlannerWarnings(request, pool),
        };
    }

    public IReadOnlyList<DadAcquiredCharacter> ResolveParticipants(DadRunPlan plan, DadCharacterPool pool, out string blocker)
    {
        blocker = string.Empty;

        if (plan.RequiredParticipantCount <= 1 || plan.Orchestration.LocalOnlyOverride)
        {
            var local = pool.Characters.FirstOrDefault(static character => character.Source == DadCharacterSource.LocalRuntime);
            if (local == null)
            {
                blocker = "No local Dad character is available.";
                return [];
            }

            return [local.Clone()];
        }

        var filteredPool = BuildFilteredPool(plan, pool);
        var plannerOptions = new DadPresetPlannerOptions
        {
            ActivityMode = plan.CompositeModuleId switch
            {
                DadModuleId.Msq => DadPlannerActivityMode.Msq,
                DadModuleId.DutySupport => DadPlannerActivityMode.DutySupport,
                DadModuleId.Trust => DadPlannerActivityMode.Trust,
                DadModuleId.PremadeDuty => DadPlannerActivityMode.PremadeDuty,
                DadModuleId.DailyMsq => DadPlannerActivityMode.DailyMsqPremade,
                DadModuleId.Blunderville => DadPlannerActivityMode.Blunderville,
                DadModuleId.Mogtome => DadPlannerActivityMode.Mogtome,
                DadModuleId.Commendation => DadPlannerActivityMode.Commendation,
                DadModuleId.Astrope => DadPlannerActivityMode.Astrope,
                DadModuleId.CustomDuty => DadPlannerActivityMode.CustomDuty,
                DadModuleId.Duty => DadPlannerActivityMode.LocalDuty,
                _ => DadPlannerActivityMode.DutyPremade,
            },
            PresetName = "Dad Live Roster",
            OperatorMode = DadPlannerOperatorMode.RemotePartyPlan,
            ConnectedOnly = true,
            SameDatacenterOnly = true,
            AllowStaleForPlanning = false,
            TransportOwner = ResolveTransportOwner(plan.CompositeModuleId),
            QueueAuthority = ResolveQueueAuthority(plan.CompositeModuleId),
        };

        var preview = presetProviderService.BuildPlannerPreview(filteredPool, plannerOptions);
        var resolved = preview.SelectedCharacters
            .Where(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey))
            .Select(slot => preview.AvailableCharacters.FirstOrDefault(character =>
                string.Equals(character.CharacterKey, slot.CharacterKey, StringComparison.OrdinalIgnoreCase)))
            .Where(static character => character != null)
            .Select(static character => character!.Clone())
            .DistinctBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (resolved.Count < plan.RequiredParticipantCount)
        {
            blocker = preview.Blockers.Count > 0
                ? string.Join(" | ", preview.Blockers)
                : $"Only {resolved.Count} typed participant(s) resolved for required size {plan.RequiredParticipantCount}.";
        }

        return resolved;
    }

    private static DadCharacterPool BuildFilteredPool(DadRunPlan plan, DadCharacterPool pool)
    {
        var requiredAccounts = plan.Orchestration.RequiredAccountKeys
            .Select(static key => key.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferredAccounts = plan.Orchestration.PreferredAccountKeys
            .Select(static key => key.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredCharacters = plan.Orchestration.RequiredCharacterKeys
            .Select(static key => key.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferredCharacters = plan.Orchestration.PreferredCharacterKeys
            .Select(static key => key.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filteredCharacters = pool.Characters
            .Where(character =>
            {
                if (requiredAccounts.Count > 0 &&
                    !requiredAccounts.Contains(character.AccountId) &&
                    !requiredAccounts.Contains(character.AccountAlias))
                {
                    return false;
                }

                if (requiredCharacters.Count > 0 &&
                    !requiredCharacters.Contains(character.CharacterKey))
                {
                    return false;
                }

                return true;
            })
            .OrderByDescending(character =>
                preferredAccounts.Contains(character.AccountId) ||
                preferredAccounts.Contains(character.AccountAlias))
            .ThenByDescending(character => preferredCharacters.Contains(character.CharacterKey))
            .ThenByDescending(static character => character.Source == DadCharacterSource.LocalRuntime)
            .ThenBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .Select(static character => character.Clone())
            .ToList();

        return new DadCharacterPool
        {
            LastUpdatedUtc = pool.LastUpdatedUtc,
            Characters = filteredCharacters,
            PeerTransport = pool.PeerTransport,
            XadbStatus = pool.XadbStatus,
            LastSummary = pool.LastSummary,
        };
    }

    private static DadTransportOwner ResolveTransportOwner(DadModuleId moduleId)
        => moduleId switch
        {
            DadModuleId.Commendation or DadModuleId.Astrope => DadTransportOwner.AuraFarmer,
            DadModuleId.Mogtome => DadTransportOwner.Mogtome,
            DadModuleId.Blunderville => DadTransportOwner.Blunderville,
            DadModuleId.Msq or DadModuleId.PremadeDuty or DadModuleId.DailyMsq => DadTransportOwner.LanParty,
            _ => DadTransportOwner.DadDirect,
        };

    private static DadQueueAuthority ResolveQueueAuthority(DadModuleId moduleId)
        => moduleId switch
        {
            DadModuleId.Commendation or DadModuleId.Astrope => DadQueueAuthority.AuraFarmer,
            DadModuleId.Mogtome => DadQueueAuthority.Mogtome,
            DadModuleId.Blunderville => DadQueueAuthority.Blunderville,
            DadModuleId.Msq or DadModuleId.PremadeDuty or DadModuleId.DailyMsq => DadQueueAuthority.Leader,
            _ => DadQueueAuthority.LocalOnly,
        };

    private static List<string> BuildPlannerWarnings(DadRunRequest request, DadCharacterPool pool)
    {
        var warnings = new List<string>();
        if (pool.Characters.Count == 0)
            warnings.Add("Dad character pool is empty at plan time.");

        if (request.Orchestration.LocalOnlyOverride)
            warnings.Add("Local-only mode ignores connected Dad workers until changed.");

        if (request.Dungeon?.QueueViaLanParty == true)
            warnings.Add("Premade dungeon routing stays inside Dad's internal premade lane.");

        if (request.DailyMsq != null)
            warnings.Add("Daily MSQ routes through Dad's internal premade lane.");

        if (request.Msq != null || request.PremadeDuty != null || request.Mogtome != null)
            warnings.Add("MSQ, Premade Duty, and MOGTOME require Server Dad authority and exact typed party workers.");

        if (request.DutySupport != null || request.Trust != null || request.CustomDuty != null)
            warnings.Add("Duty Support, Trust, and Custom Duty remain local typed duty lanes until guarded live start is enabled.");

        if (request.Blunderville != null)
            warnings.Add("Blunderville routes through Dad-owned local lane with helper integration deferred.");

        if (request.Commendation != null || request.Astrope != null)
            warnings.Add("Commendation and Astrope route through Dad's internal aura lane.");

        return warnings;
    }
}
