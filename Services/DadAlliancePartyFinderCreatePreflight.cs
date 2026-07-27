using dad.Models;

namespace dad.Services;

internal sealed record DadAlliancePfCreatePreflightInput
{
    public bool HasConcretePreset { get; init; }
    public DadAlliancePresetValidation Validation { get; init; } = new();
    public bool RecruitmentActive { get; init; }
    public string OperationalBlocker { get; init; } = string.Empty;
    public bool TargetsResolved { get; init; } = true;
    public string TargetBlocker { get; init; } = string.Empty;
    public bool HostIsAllianceA { get; init; } = true;
}

internal readonly record struct DadAlliancePfCreatePreflightDecision(
    bool Ready,
    string Blocker);

internal static class DadAlliancePartyFinderCreatePreflight
{
    internal const string PresetBlocker =
        "Select a concrete saved preset before creating an alliance party.";
    internal const string ActiveRecruitmentBlocker =
        "Stop the active alliance recruitment before creating another party.";
    internal const string HostBlocker =
        "The current PF creator must be the effective Alliance-A host.";

    public static DadAlliancePfCreatePreflightDecision Evaluate(
        DadAlliancePfCreatePreflightInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.HasConcretePreset)
            return Blocked(PresetBlocker);
        if (!input.Validation.IsValid)
            return Blocked(input.Validation.Summary);
        if (input.RecruitmentActive)
            return Blocked(ActiveRecruitmentBlocker);
        if (!string.IsNullOrWhiteSpace(input.OperationalBlocker))
            return Blocked(input.OperationalBlocker);
        if (!input.TargetsResolved)
            return Blocked(input.TargetBlocker);
        if (!input.HostIsAllianceA)
            return Blocked(HostBlocker);
        return new DadAlliancePfCreatePreflightDecision(true, string.Empty);
    }

    public static DadAlliancePartyFinderStatus SelectLocalDisplay(
        DadAlliancePartyFinderStatus live,
        DadAlliancePartyFinderStatus preflight)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(preflight);
        return live.CreateRejected || !string.IsNullOrWhiteSpace(live.RecruitmentId)
            ? live
            : preflight;
    }

    private static DadAlliancePfCreatePreflightDecision Blocked(string blocker)
        => new(
            false,
            string.IsNullOrWhiteSpace(blocker)
                ? "Alliance Party Finder Create is not ready."
                : blocker);
}
