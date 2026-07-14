using dad.Models;

namespace dad.Services;

public static class DadPlannerGroupUpdateRules
{
    public static void ApplyPlannerFields(
        DadPlannerGroup target,
        DadPlannerGroup source,
        DateTime updatedAtUtc)
    {
        target.DisplayName = source.DisplayName;
        target.RunFamily = source.RunFamily;
        target.ActivityMode = source.ActivityMode;
        target.OperatorMode = source.OperatorMode;
        target.ConnectedOnly = source.ConnectedOnly;
        target.SameDatacenterOnly = source.SameDatacenterOnly;
        target.AllowStaleForPlanning = source.AllowStaleForPlanning;
        target.TransportOwner = source.TransportOwner;
        target.QueueAuthority = source.QueueAuthority;
        target.InviteAuthority = DadInviteAuthority.PresetLeader;
        target.DutyContentFinderConditionId = source.DutyContentFinderConditionId;
        target.DutyDisplayName = source.DutyDisplayName;
        target.DutyUnsynced = source.DutyUnsynced;
        target.DutyExpectedPartySize = source.DutyExpectedPartySize;
        target.RouletteTarget = source.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        target.MogtomePreset = source.MogtomePreset;
        target.MogtomeDutyPolicy = source.MogtomeDutyPolicy;
        target.RefreshTrustNpcLevels = source.RefreshTrustNpcLevels;
        target.StopPolicy = source.StopPolicy.Clone();
        target.CompletionActions = source.CompletionActions?.Clone();
        target.UpdatedAtUtc = EnsureUtc(updatedAtUtc);
    }

    public static List<DadPlannerGroupSlot> RefreshSlotsPreservingOperationalSettings(
        IEnumerable<DadPlannerGroupSlot>? existingSlots,
        IEnumerable<DadPlannerGroupSlot>? refreshedSlots)
    {
        var existing = DadPlannerSlotRules.NormalizeGroupSlots(existingSlots);
        var refreshed = DadPlannerSlotRules.NormalizeGroupSlots(refreshedSlots);
        var consumed = new HashSet<int>();
        var merged = new List<DadPlannerGroupSlot>(refreshed.Count);

        foreach (var refreshedSlot in refreshed)
        {
            var matchIndex = FindMatchingRow(existing, consumed, refreshedSlot);
            var result = Clone(refreshedSlot);
            if (matchIndex >= 0)
            {
                var prior = existing[matchIndex];
                consumed.Add(matchIndex);
                result.RequiredAccountKey = prior.RequiredAccountKey;
                result.RequiredCharacterKey = prior.RequiredCharacterKey;
                result.RequiredJobId = prior.RequiredJobId;
                result.AdsLootMode = prior.AdsLootMode;
                result.LevelSeekTarget = prior.LevelSeekTarget;
                result.WakePolicy = prior.WakePolicy;
                result.LaunchProfileId = prior.LaunchProfileId;
                result.CharacterLoadInstruction = prior.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction();
                result.AllowSubstitution = prior.AllowSubstitution;
            }
            else
            {
                result.WakePolicy = DadSchedulerWakePolicy.LaunchIfOffline;
            }

            merged.Add(result);
        }

        return DadPlannerSlotRules.NormalizeGroupSlots(merged);
    }

    private static int FindMatchingRow(
        IReadOnlyList<DadPlannerGroupSlot> existing,
        IReadOnlySet<int> consumed,
        DadPlannerGroupSlot refreshed)
    {
        for (var index = 0; index < existing.Count; index++)
        {
            if (consumed.Contains(index))
                continue;

            var candidate = existing[index];
            if (candidate.IsSubstitute == refreshed.IsSubstitute &&
                string.Equals(candidate.SlotId, refreshed.SlotId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static DadPlannerGroupSlot Clone(DadPlannerGroupSlot source)
        => new()
        {
            SlotId = source.SlotId,
            IsSubstitute = source.IsSubstitute,
            RequiredRole = source.RequiredRole,
            RequiredAccountKey = source.RequiredAccountKey,
            RequiredCharacterKey = source.RequiredCharacterKey,
            RequiredJobId = source.RequiredJobId,
            AdsLootMode = source.AdsLootMode,
            LevelSeekTarget = source.LevelSeekTarget,
            WakePolicy = source.WakePolicy,
            LaunchProfileId = source.LaunchProfileId?.Trim() ?? string.Empty,
            CharacterLoadInstruction = source.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction(),
            AllowSubstitution = source.AllowSubstitution,
        };

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
