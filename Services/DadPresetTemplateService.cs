using dad.Models;

namespace dad.Services;

// Feature batch B (dadfeatures20260620b line 56): reusable preset templates.
// A template is a planner group whose slots carry roles but not specific characters.
internal static class DadPresetTemplateService
{
    public static DadPlannerGroup CreateTemplateFrom(DadPlannerGroup group, string templateName, DateTime nowUtc)
    {
        var template = CloneGroup(group) ?? new DadPlannerGroup();
        template.GroupId = Guid.NewGuid().ToString("N");
        template.IsTemplate = true;
        template.DisplayName = string.IsNullOrWhiteSpace(templateName) ? $"{group.DisplayName} (template)" : templateName.Trim();
        template.ScheduleEnabled = false;
        template.NextEligibleTimeUtc = null;
        template.CreatedAtUtc = nowUtc;
        template.UpdatedAtUtc = nowUtc;
        template.InviteAuthority = DadInviteAuthority.PresetLeader;
        template.Slots = DadPlannerSlotRules.NormalizeGroupSlots(template.Slots);

        foreach (var slot in template.Slots)
        {
            slot.RequiredAccountKey = new DadAccountKey(string.Empty);
            slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
            slot.RequiredJobId = null;
            slot.SharedIdentity = null;
        }
        template.SharedStopTargetIdentityToken = string.Empty;
        template.StopPolicy.TargetCharacterKey = new DadCharacterKey(string.Empty);
        template.StopPolicy.TargetCharacterLabel = string.Empty;

        return template;
    }

    public static DadPlannerGroup Instantiate(DadPlannerGroup template, DadCharacterPool pool, DateTime nowUtc)
    {
        var instance = CloneGroup(template) ?? new DadPlannerGroup();
        instance.GroupId = Guid.NewGuid().ToString("N");
        instance.IsTemplate = false;
        instance.DisplayName = $"{template.DisplayName} (instance)";
        instance.ScheduleEnabled = false;
        instance.NextEligibleTimeUtc = null;
        instance.CreatedAtUtc = nowUtc;
        instance.UpdatedAtUtc = nowUtc;
        instance.InviteAuthority = DadInviteAuthority.PresetLeader;
        instance.Slots = DadPlannerSlotRules.NormalizeGroupSlots(instance.Slots);

        var available = (pool?.Characters ?? [])
            .Where(static character => !string.IsNullOrWhiteSpace(character.CharacterKey))
            .OrderByDescending(static character => character.IsLiveConnected)
            .ThenByDescending(static character => character.Readiness == DadReadinessState.Ready)
            .ThenByDescending(static character => character.Source == DadCharacterSource.LocalRuntime)
            .ThenBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in instance.Slots)
        {
            if (!slot.RequiredCharacterKey.IsEmpty)
            {
                var pinned = available.FirstOrDefault(character =>
                    string.Equals(character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase));
                if (pinned != null)
                {
                    var account = ResolveAccountKey(pinned);
                    if (slot.RequiredAccountKey.IsEmpty && !string.IsNullOrWhiteSpace(account))
                        slot.RequiredAccountKey = new DadAccountKey(account);
                    used.Add(pinned.CharacterKey);
                    if (!string.IsNullOrWhiteSpace(account))
                        usedAccounts.Add(account);
                }
                else
                {
                    used.Add(slot.RequiredCharacterKey.Value);
                    if (!slot.RequiredAccountKey.IsEmpty)
                        usedAccounts.Add(slot.RequiredAccountKey.Value);
                }

                continue;
            }

            var match = available.FirstOrDefault(character =>
                !used.Contains(character.CharacterKey) &&
                !IsAccountUsed(character, usedAccounts) &&
                RoleMatches(slot.RequiredRole, ClassifyRole(character)));

            if (match == null)
                continue;

            slot.RequiredCharacterKey = new DadCharacterKey(match.CharacterKey);
            var accountKey = ResolveAccountKey(match);
            if (!string.IsNullOrWhiteSpace(accountKey))
                slot.RequiredAccountKey = new DadAccountKey(accountKey);
            used.Add(match.CharacterKey);
            if (!string.IsNullOrWhiteSpace(accountKey))
                usedAccounts.Add(accountKey);
        }

        return instance;
    }

    public static int CountAssignedSlots(DadPlannerGroup group)
        => DadPlannerSlotRules.NormalizeGroupSlots(group.Slots)
            .Count(static slot => !slot.IsSubstitute && !slot.RequiredCharacterKey.IsEmpty);

    private static bool RoleMatches(DadPartyRole required, DadPartyRole actual)
        => required switch
        {
            DadPartyRole.Any => true,
            DadPartyRole.Dps => actual is DadPartyRole.Melee or DadPartyRole.PhysicalRanged or DadPartyRole.Caster,
            _ => required == actual,
        };

    private static DadPartyRole ClassifyRole(DadAcquiredCharacter character)
    {
        var job = character.CurrentJobAbbrev.Trim().ToUpperInvariant();
        return job switch
        {
            "PLD" or "WAR" or "DRK" or "GNB" => DadPartyRole.Tank,
            "WHM" or "SCH" or "AST" or "SGE" => DadPartyRole.Healer,
            "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR" => DadPartyRole.Melee,
            "BRD" or "MCH" or "DNC" => DadPartyRole.PhysicalRanged,
            "BLM" or "SMN" or "RDM" or "PCT" => DadPartyRole.Caster,
            "BLU" => DadPartyRole.Limited,
            _ => DadPartyRole.Any,
        };
    }

    private static bool IsAccountUsed(DadAcquiredCharacter character, HashSet<string> usedAccounts)
    {
        var accountKey = ResolveAccountKey(character);
        return !string.IsNullOrWhiteSpace(accountKey) && usedAccounts.Contains(accountKey);
    }

    private static string ResolveAccountKey(DadAcquiredCharacter character)
        => !string.IsNullOrWhiteSpace(character.AccountId)
            ? character.AccountId
            : character.AccountAlias;

    private static DadPlannerGroup? CloneGroup(DadPlannerGroup group)
        => DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(group));
}
