using dad.Models;

namespace dad.Services;

public readonly record struct DadAdaptiveDutyProjection(
    int PrimaryRowCount,
    int ExpectedPartySize,
    bool UsesPremadeExecutor)
{
    public bool IsAdaptive => PrimaryRowCount > 0;
}

/// <summary>
/// Resolves the execution shape of a saved Duty preset without mutating it. Substitute
/// rows are alternatives for their primary slot and therefore never add party members.
/// </summary>
public static class DadAdaptiveDutyProjectionRules
{
    public static DadAdaptiveDutyProjection Resolve(
        DadPlannerActivityMode activityMode,
        DadPlannerGroup? group)
    {
        if (activityMode != DadPlannerActivityMode.LocalDuty || group == null)
            return new DadAdaptiveDutyProjection(0, 1, false);

        var primaryCount = DadPlannerSlotRules.CountPrimarySlots(group.Slots);
        var partySize = Math.Max(1, primaryCount);
        return new DadAdaptiveDutyProjection(primaryCount, partySize, primaryCount >= 2);
    }

    public static void PopulateDutyTask(
        DadRunRequest request,
        DadAdaptiveDutyProjection projection,
        uint contentFinderConditionId,
        string dutyName,
        bool unsynced)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (projection.UsesPremadeExecutor)
        {
            request.PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = contentFinderConditionId,
                DutyName = dutyName,
                Unsynced = unsynced,
                ExpectedPartySize = projection.ExpectedPartySize,
                Attempts = 1,
            };
            request.Dungeon = null;
            return;
        }

        request.Dungeon = new DadDungeonTask
        {
            Count = 1,
            Frequency = DadRunRequestOptions.FrequencyPerArRun,
            ContentFinderConditionId = contentFinderConditionId,
            SelectedDungeon = dutyName,
            ExecutionPreference = DadRunRequestOptions.TrustThenDutySupport,
            Unsynced = unsynced,
        };
        request.PremadeDuty = null;
    }
}
