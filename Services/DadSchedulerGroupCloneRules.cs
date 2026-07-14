using dad.Models;

namespace dad.Services;

public static class DadSchedulerGroupCloneRules
{
    public static IReadOnlyList<DadLaunchProfile> CloneNormalizedLaunchProfiles(
        IEnumerable<DadLaunchProfile>? profiles)
        => (profiles ?? [])
            .Where(static profile => profile != null)
            .Select(static profile => profile.Clone().Normalize())
            .ToList();

    public static DadPlannerGroup CloneWithSlots(
        DadPlannerGroup source,
        IEnumerable<DadPlannerGroupSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(slots);

        return new DadPlannerGroup
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
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = source.DutyDisplayName,
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            RouletteTarget = source.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette },
            MogtomePreset = source.MogtomePreset,
            MogtomeDutyPolicy = source.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = source.RefreshTrustNpcLevels,
            StopPolicy = source.StopPolicy.Clone(),
            CompletionActions = source.CompletionActions?.Clone(),
            Slots = slots.Select(CloneSlot).ToList(),
            IsTemplate = source.IsTemplate,
            ScheduleEnabled = source.ScheduleEnabled,
            ScheduleCadenceHours = source.ScheduleCadenceHours,
            NextEligibleTimeUtc = source.NextEligibleTimeUtc,
            ScheduleRequester = source.ScheduleRequester,
            SchedulePriority = source.SchedulePriority,
            MapRunTemplate = source.MapRunTemplate,
            MapMode = source.MapMode,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
        };
    }

    public static DadPlannerGroupSlot CloneSlot(DadPlannerGroupSlot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new DadPlannerGroupSlot
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
            LaunchProfileId = source.LaunchProfileId,
            CharacterLoadInstruction = source.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction(),
            AllowSubstitution = source.AllowSubstitution,
        };
    }
}
