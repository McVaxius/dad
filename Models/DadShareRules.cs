namespace dad.Models;

public static class DadSharedPlanRules
{
    public const string RemapBlocker = "Shared crew placeholders must be remapped to local accounts and characters in the Crew editor before this Plan can validate or run.";

    public static bool HasUnresolvedPlaceholders(DadPlannerGroup? group)
        => group != null &&
           (group.Slots.Any(static slot => slot.SharedIdentity != null) ||
            !string.IsNullOrWhiteSpace(group.SharedStopTargetIdentityToken));

    public static List<string> BuildBlockers(DadPlannerGroup? group)
    {
        if (group == null || !HasUnresolvedPlaceholders(group))
            return [];

        var unresolvedRows = group.Slots.Count(static slot => slot.SharedIdentity != null);
        var suffix = unresolvedRows == 0
            ? " The shared stop target is still unresolved."
            : $" {unresolvedRows} crew row(s) remain unresolved.";
        return [$"{RemapBlocker}{suffix}"];
    }

    public static void CompleteAccountOnlyRemap(DadPlannerGroup group, DadPlannerGroupSlot slot)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.SharedIdentity is not { RequiresCharacter: false } placeholder || slot.RequiredAccountKey.IsEmpty)
            return;

        CompleteStopTarget(group, placeholder, slot.RequiredCharacterKey);
        slot.SharedIdentity = null;
    }

    public static void CompleteCharacterRemap(DadPlannerGroup group, DadPlannerGroupSlot slot)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.SharedIdentity is not { } placeholder ||
            slot.RequiredAccountKey.IsEmpty ||
            slot.RequiredCharacterKey.IsEmpty)
        {
            return;
        }

        CompleteStopTarget(group, placeholder, slot.RequiredCharacterKey);
        slot.SharedIdentity = null;
    }

    public static void ReconcileStopTarget(DadPlannerGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.StopPolicy ??= new DadRunStopPolicy();
        if (group.StopPolicy.Mode != DadPlannerStopMode.TargetLevel ||
            !group.StopPolicy.TargetCharacterKey.IsEmpty)
        {
            group.SharedStopTargetIdentityToken = string.Empty;
        }
    }

    private static void CompleteStopTarget(
        DadPlannerGroup group,
        DadSharedIdentityPlaceholder placeholder,
        DadCharacterKey localCharacterKey)
    {
        group.StopPolicy ??= new DadRunStopPolicy();
        if (string.IsNullOrWhiteSpace(placeholder.IdentityToken) ||
            !string.Equals(
                group.SharedStopTargetIdentityToken,
                placeholder.IdentityToken,
                StringComparison.Ordinal))
        {
            return;
        }

        group.StopPolicy.TargetCharacterKey = localCharacterKey;
        group.StopPolicy.TargetCharacterLabel = localCharacterKey.Value;
        group.SharedStopTargetIdentityToken = string.Empty;
    }
}

public static class DadLegacyActivityRules
{
    public const string MsqUnsupportedBlocker = "MSQ Story Plans are retained for compatibility but are unsupported. Select another activity explicitly before validation or run.";

    public static bool IsCreationActivity(DadPlannerActivityMode activityMode)
        => activityMode != DadPlannerActivityMode.Msq;

    public static string GetValidationBlocker(DadPlannerActivityMode activityMode)
        => activityMode == DadPlannerActivityMode.Msq ? MsqUnsupportedBlocker : string.Empty;
}
