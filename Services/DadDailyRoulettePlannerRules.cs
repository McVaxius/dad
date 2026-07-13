using dad.Models;

namespace dad.Services;

public sealed class DadPlannerRouletteResolution
{
    public DadQueueTarget Target { get; init; } = new() { Kind = DadQueueTargetKind.Roulette };
    public DadPlannerRouletteOption? Option { get; init; }
    public bool IsAvailable { get; init; }
    public bool ResolvedLegacyMainScenario { get; init; }
    public string Blocker { get; init; } = string.Empty;
}

public static class DadDailyRoulettePlannerRules
{
    public const int RequiredPartySize = 4;

    public static DadPlannerRouletteResolution ResolveTarget(
        DadQueueTarget? source,
        IReadOnlyList<DadPlannerRouletteOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var target = source?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        target.Key = target.Key?.Trim() ?? string.Empty;
        target.DisplayName = target.DisplayName?.Trim() ?? string.Empty;

        if (target.Kind != DadQueueTargetKind.Roulette)
        {
            return new DadPlannerRouletteResolution
            {
                Target = target,
                Blocker = $"Daily Roulette target kind must be Roulette, not {target.Kind}.",
            };
        }

        var resolvedLegacyMainScenario = target.RouletteId == 0 &&
            string.Equals(
                target.Key,
                DadRouletteCatalogProjection.MainScenarioLegacyKey,
                StringComparison.OrdinalIgnoreCase);
        if (resolvedLegacyMainScenario)
            target.RouletteId = DadRouletteCatalogProjection.MainScenarioRouletteId;

        if (target.RouletteId == 0)
        {
            return new DadPlannerRouletteResolution
            {
                Target = target,
                Blocker = "Daily Roulette requires a roulette selection.",
            };
        }

        var option = DadRouletteCatalogProjection.ResolveEligibleOption(options, target.RouletteId);
        if (option != null)
        {
            var canonicalTarget = option.ToQueueTarget();
            canonicalTarget.SchemaVersion = Math.Max(1, target.SchemaVersion);
            return new DadPlannerRouletteResolution
            {
                Target = canonicalTarget,
                Option = option.Clone(),
                IsAvailable = true,
                ResolvedLegacyMainScenario = resolvedLegacyMainScenario,
            };
        }

        if (string.IsNullOrWhiteSpace(target.Key))
            target.Key = DadRouletteCatalogProjection.BuildCanonicalKey(target.RouletteId);
        if (string.IsNullOrWhiteSpace(target.DisplayName))
            target.DisplayName = $"Roulette #{target.RouletteId}";

        var unavailableReason = target.RouletteId > byte.MaxValue
            ? $"Roulette #{target.RouletteId} is outside the supported Duty Finder byte range."
            : $"{target.DisplayName} #{target.RouletteId} is unavailable in the current eligible roulette catalog.";
        return new DadPlannerRouletteResolution
        {
            Target = target,
            Option = new DadPlannerRouletteOption
            {
                RouletteId = target.RouletteId,
                Key = target.Key,
                DisplayName = target.DisplayName,
                IsAvailable = false,
                UnavailableReason = unavailableReason,
            },
            Blocker = unavailableReason,
            ResolvedLegacyMainScenario = resolvedLegacyMainScenario,
        };
    }

    public static DadDailyMsqTask BuildWireCompatibleTask(DadQueueTarget target)
        => new()
        {
            LanPartyPreset = "Daily Roulette",
            QueueTarget = target.Clone(),
        };
}
