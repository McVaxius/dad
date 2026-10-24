namespace dad.Models;

/// <summary>Local Client Dad scheduling is limited to one exact own-account NPC duty slot.</summary>
public static class DadLocalDutySupportRules
{
    public const string CoordinatorOnlyReason =
        "Client Dad may schedule solo Duty Support or Trust for its own account; other work requires Dad Coordinator.";

    public static bool CanRunPreset(DadPlannerGroup? group, string? localAccountId)
    {
        if (group == null || string.IsNullOrWhiteSpace(localAccountId) ||
            group.ActivityMode is not (DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust) ||
            group.RunFamily != DadPlannerRunFamily.LevelingNpc || group.TransportOwner != DadTransportOwner.DadDirect ||
            group.AutoPartyFormationOnly || group.IsTemplate || DadShoppingAssociationRules.PlanAssignments(group).Count > 0 ||
            group.DutyExpectedPartySize != 1 || group.Slots is not { Count: > 0 })
            return false;

        var primary = group.Slots.Where(static slot => !slot.IsSubstitute).ToList();
        return primary.Count == 1 &&
               string.Equals(primary[0].SlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase) &&
               group.Slots.All(slot =>
                   !slot.RequiredAccountKey.IsEmpty && !slot.RequiredCharacterKey.IsEmpty &&
                   slot.SharedIdentity == null &&
                   string.Equals(slot.SlotId, primary[0].SlotId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(slot.RequiredAccountKey.Value, localAccountId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanRunSchedule(
        DadScheduleDefinition? schedule,
        IEnumerable<DadPlannerGroup> groups,
        string? localAccountId)
    {
        if (schedule?.Entries is not { Count: > 0 } || DadShoppingAssociationRules.ScheduleAssignments(schedule).Count > 0)
            return false;
        var candidates = groups.ToList();
        return schedule.Entries.All(entry =>
        {
            var matches = candidates.Where(group =>
                !string.IsNullOrWhiteSpace(entry.GroupId) &&
                string.Equals(group.GroupId, entry.GroupId, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 && CanRunPreset(matches[0], localAccountId);
        });
    }

    public static bool CanRunQueuedJob(DadScheduledCrewJob job, DadPlannerGroup? group, string? localAccountId)
        => job.JobType == DadSchedulerJobType.ScheduledPreset && CanRunPreset(group, localAccountId);
}
