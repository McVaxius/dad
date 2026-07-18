using dad.Models;

namespace dad.Services;

internal sealed record DadLevelingModeActivationResult(
    bool Accepted,
    bool Enabled,
    DadPlannerRunFamily NormalizedRunFamily,
    DadPlannerActivityMode NormalizedActivityMode,
    string Summary);

internal static class DadLevelingModeActivationRules
{
    public const string ValidLaneSummary =
        "Choose Leveling / NPC -> Duty Support or Trust, or Duty Finder -> Premade Duty.";

    public static bool TryNormalizeSupportedDraft(
        DadPlannerRunFamily runFamily,
        DadPlannerActivityMode activityMode,
        out DadPlannerRunFamily normalizedRunFamily,
        out DadPlannerActivityMode normalizedActivityMode)
    {
        normalizedActivityMode = activityMode switch
        {
            DadPlannerActivityMode.DutySupportLeveling => DadPlannerActivityMode.DutySupport,
            DadPlannerActivityMode.TrustLeveling => DadPlannerActivityMode.Trust,
            DadPlannerActivityMode.DutyPremade => DadPlannerActivityMode.PremadeDuty,
            _ => activityMode,
        };
        normalizedRunFamily = normalizedActivityMode is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust
            ? DadPlannerRunFamily.LevelingNpc
            : DadPlannerRunFamily.DutyFinder;

        return runFamily == normalizedRunFamily
               && normalizedActivityMode is DadPlannerActivityMode.DutySupport
                   or DadPlannerActivityMode.Trust
                   or DadPlannerActivityMode.PremadeDuty;
    }

    public static DadLevelingModeActivationResult Apply(
        DadPlannerGroup target,
        DadPlannerGroup draft,
        bool enabled,
        DateTime updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(draft);

        if (!enabled)
        {
            if (target.LevelingMode != null)
                target.LevelingMode.Enabled = false;
            return new DadLevelingModeActivationResult(
                true,
                false,
                target.RunFamily,
                target.ActivityMode,
                "Leveling Mode disabled; saved planner fields and settings were preserved.");
        }

        if (!TryNormalizeSupportedDraft(
                draft.RunFamily,
                draft.ActivityMode,
                out var normalizedRunFamily,
                out var normalizedActivityMode))
        {
            return new DadLevelingModeActivationResult(
                false,
                target.LevelingMode?.Enabled == true,
                target.RunFamily,
                target.ActivityMode,
                ValidLaneSummary);
        }

        if (draft.StopPolicy == null)
        {
            return new DadLevelingModeActivationResult(
                false,
                target.LevelingMode?.Enabled == true,
                target.RunFamily,
                target.ActivityMode,
                "The visible planner draft has no stop policy and cannot be saved atomically.");
        }

        // Stage the exact established planner-field copy before touching the selected preset. This keeps
        // rejection and unexpected clone failures atomic while leaving crew and Leveling settings outside
        // the planner-field ownership boundary.
        var staged = new DadPlannerGroup();
        try
        {
            DadPlannerGroupUpdateRules.ApplyPlannerFields(staged, draft, updatedAtUtc);
        }
        catch (Exception ex)
        {
            return new DadLevelingModeActivationResult(
                false,
                target.LevelingMode?.Enabled == true,
                target.RunFamily,
                target.ActivityMode,
                $"The visible planner draft could not be staged: {ex.GetType().Name}.");
        }

        var levelingSettings = target.LevelingMode ?? new DadLevelingModeOptions();
        DadPlannerGroupUpdateRules.ApplyPlannerFields(target, staged, updatedAtUtc);
        target.LevelingMode = levelingSettings;
        target.LevelingMode.Enabled = true;
        return new DadLevelingModeActivationResult(
            true,
            true,
            normalizedRunFamily,
            normalizedActivityMode,
            "Leveling Mode enabled from the visible planner draft.");
    }
}
