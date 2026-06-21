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

        if (modules.Count == 0)
            return null;

        if (!ValidateLocalNpcDutyRunner(request, pool, out rejectionReason))
            return null;

        if (!ValidateStopPolicyAtStart(request, pool, out rejectionReason))
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

        if (!ValidateRequiredRuntimeParticipants(request, pool, configuration.PartyValidationOverrideEnabled, out rejectionReason))
            return null;

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
                string.Equals(character.CharacterKey, slot.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                (slot.RequiredAccountKey.IsEmpty || MatchesAccountKey(character, slot.RequiredAccountKey.Value))))
            .Where(static character => character != null)
            .Select(static character => character!.Clone())
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
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

    private static DadTransportOwner ResolveTransportOwner(DadModuleId moduleId)
        => moduleId switch
        {
            DadModuleId.PremadeDuty or DadModuleId.DailyMsq => DadTransportOwner.LanParty,
            _ => DadTransportOwner.DadDirect,
        };

    private static DadQueueAuthority ResolveQueueAuthority(DadModuleId moduleId)
        => moduleId switch
        {
            DadModuleId.Mogtome or DadModuleId.Commendation or DadModuleId.Astrope
                or DadModuleId.PremadeDuty or DadModuleId.DailyMsq => DadQueueAuthority.Leader,
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

        if (request.Dungeon is { QueueViaLanParty: false })
            warnings.Add("Local Duty routes through Dad-owned guarded regular Duty Finder queue execution.");

        if (request.DailyMsq != null)
            warnings.Add("Daily MSQ routes through Dad's internal premade lane.");

        if (request.PremadeDuty != null || request.Mogtome != null)
            warnings.Add("Premade Duty and MOGTOME require Server Dad authority and exact typed party workers.");

        if (request.Msq != null)
            warnings.Add("MSQ solo progression uses selected duty with Trust then Duty Support fallback.");

        if (request.DutySupport != null || request.Trust != null)
            warnings.Add("Duty Support and Trust route through Dad-owned guarded native local NPC duty lanes.");

        if (request.CustomDuty != null)
            warnings.Add("Custom Duty uses typed CFC selection and routes by configured party size.");

        if (request.Blunderville != null)
            warnings.Add("Blunderville remains Dad-owned but blocks until guarded Gold Saucer callbacks are available.");

        if (request.Commendation != null || request.Astrope != null)
            warnings.Add("Commendation and Astrope remain Dad-owned; AuraFarmer is not required.");

        return warnings;
    }
}
