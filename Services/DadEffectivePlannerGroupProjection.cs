using dad.Models;

namespace dad.Services;

/// <summary>
/// Builds the non-persistent group shape consumed by planner preview, request validation,
/// scheduler preflight, and scheduler execution. The saved group is never rewritten.
/// </summary>
public static class DadEffectivePlannerGroupProjection
{
    public static DadPlannerGroup Project(
        DadPlannerGroup source,
        DadPlannerActivityMode activityMode,
        int requestedPartySize)
    {
        ArgumentNullException.ThrowIfNull(source);

        var normalizedRows = DadPlannerSlotRules.NormalizeGroupSlots(source.Slots);
        var logicalSlotCount = source.AutoPartyFormationOnly
            ? null
            : ResolveLogicalSlotCount(activityMode, requestedPartySize);
        var projectedRows = logicalSlotCount.HasValue
            ? normalizedRows
                .Where(row =>
                    DadPlannerSlotRules.TryParseStrictSlotNumber(row.SlotId, out var slotNumber) &&
                    slotNumber <= logicalSlotCount.Value)
                .ToList()
            : normalizedRows;

        return DadSchedulerGroupCloneRules.CloneWithSlots(source, projectedRows);
    }

    public static DadPlannerGroup BindResolvedSchedulerSlots(
        DadPlannerGroup projectedGroup,
        IReadOnlyList<DadPresetCharacterSlot> resolvedSlots)
    {
        ArgumentNullException.ThrowIfNull(projectedGroup);
        ArgumentNullException.ThrowIfNull(resolvedSlots);

        var rows = DadPlannerSlotRules.NormalizeGroupSlots(projectedGroup.Slots);
        var primaryRows = DadPlannerSlotRules.GetPrimaryRows(rows);
        var bound = new List<DadPlannerGroupSlot>(primaryRows.Count);
        foreach (var primary in primaryRows)
        {
            var resolved = resolvedSlots.FirstOrDefault(slot =>
                string.Equals(
                    DadPlannerSlotRules.NormalizeStrictSlotId(slot.SlotId),
                    primary.SlotId,
                    StringComparison.OrdinalIgnoreCase));
            var alternatives = DadPlannerSlotRules.GetRowsForSlot(rows, primary.SlotId);

            var selected = resolved == null
                ? primary
                : alternatives.FirstOrDefault(row =>
                      row.IsSubstitute == resolved.IsSubstitution &&
                      MatchesResolvedIdentity(row, resolved))
                  ?? alternatives.FirstOrDefault(row => MatchesResolvedIdentity(row, resolved))
                  ?? primary;
            var slot = DadSchedulerGroupCloneRules.CloneSlot(selected);
            slot.SlotId = primary.SlotId;
            slot.IsSubstitute = false;
            if (resolved != null)
            {
                slot.AllianceAssignment = resolved.AllianceAssignment;
                if (!string.IsNullOrWhiteSpace(resolved.CharacterKey))
                    slot.RequiredCharacterKey = new DadCharacterKey(resolved.CharacterKey.Trim());
                if (!resolved.RequiredAccountKey.IsEmpty)
                    slot.RequiredAccountKey = resolved.RequiredAccountKey;
                slot.RequiredJobId = resolved.RequiredJobId;
                slot.AdsLootMode = resolved.AdsLootMode;
                slot.LevelSeekTarget = resolved.LevelSeekTarget;
            }
            bound.Add(slot);
        }

        return DadSchedulerGroupCloneRules.CloneWithSlots(projectedGroup, bound);
    }

    public static int? ResolveLogicalSlotCount(
        DadPlannerActivityMode activityMode,
        int requestedPartySize)
        => activityMode switch
        {
            DadPlannerActivityMode.Msq => 1,
            DadPlannerActivityMode.DailyRoulette => DadDailyRoulettePlannerRules.RequiredPartySize,
            DadPlannerActivityMode.DutySupport or
            DadPlannerActivityMode.Trust or
            DadPlannerActivityMode.DutySupportLeveling or
            DadPlannerActivityMode.TrustLeveling or
            DadPlannerActivityMode.Squadron => 1,
            DadPlannerActivityMode.PremadeDuty or
            DadPlannerActivityMode.DutyPremade or
            DadPlannerActivityMode.LocalDuty or
            DadPlannerActivityMode.CustomDuty or
            DadPlannerActivityMode.VariantVvd => Math.Clamp(
                requestedPartySize <= 0 ? 1 : requestedPartySize,
                DadPlannerSlotRules.MinSlotNumber,
                DadPlannerSlotRules.MaxSlotNumber),
            _ => null,
        };

    private static bool MatchesResolvedIdentity(
        DadPlannerGroupSlot row,
        DadPresetCharacterSlot resolved)
    {
        if (!row.RequiredAccountKey.IsEmpty &&
            !resolved.RequiredAccountKey.IsEmpty &&
            !DadRosterIdentity.SameAccount(row.RequiredAccountKey, resolved.RequiredAccountKey))
        {
            return false;
        }

        return row.RequiredCharacterKey.IsEmpty ||
               string.Equals(
                   row.RequiredCharacterKey.Value,
                   resolved.CharacterKey,
                   StringComparison.OrdinalIgnoreCase);
    }
}
