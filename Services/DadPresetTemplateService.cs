using dad.Models;

namespace dad.Services;

// Feature batch B (dadfeatures20260620b line 56): reusable preset TEMPLATES.
// A template is a planner group whose slots carry roles but NOT specific characters, so the operator
// defines it once and instantiates it against whatever roster is live — no per-run character wiring.
internal static class DadPresetTemplateService
{
    // Build a template copy of a group: keep family/duty/options/slots+roles, drop the character/account bindings.
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

        foreach (var slot in template.Slots)
        {
            slot.RequiredAccountKey = new DadAccountKey(string.Empty);
            slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
        }

        return template;
    }

    // Instantiate a template into a concrete group, auto-assigning available roster characters to slots by role.
    // Slots with no role match are left empty (AllowSubstitution lets the planner fill them later).
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

        var available = (pool?.Characters ?? [])
            .Where(static character => !string.IsNullOrWhiteSpace(character.CharacterKey))
            .ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in instance.Slots)
        {
            // Respect any character a template author pinned explicitly.
            if (!slot.RequiredCharacterKey.IsEmpty)
            {
                used.Add(slot.RequiredCharacterKey.Value);
                continue;
            }

            var match = available.FirstOrDefault(character =>
                !used.Contains(character.CharacterKey) &&
                RoleMatches(slot.RequiredRole, DadPresetProviderService.ClassifyRole(character)));

            if (match == null)
                continue;

            slot.RequiredCharacterKey = new DadCharacterKey(match.CharacterKey);
            used.Add(match.CharacterKey);
        }

        return instance;
    }

    public static int CountAssignedSlots(DadPlannerGroup group)
        => group.Slots.Count(static slot => !slot.RequiredCharacterKey.IsEmpty);

    private static bool RoleMatches(DadPartyRole required, DadPartyRole actual)
        => required switch
        {
            DadPartyRole.Any => true,
            DadPartyRole.Dps => actual is DadPartyRole.Melee or DadPartyRole.PhysicalRanged or DadPartyRole.Caster,
            _ => required == actual,
        };

    private static DadPlannerGroup? CloneGroup(DadPlannerGroup group)
        => DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(group));
}
