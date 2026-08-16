using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using dad.Models;

namespace dad.Services;

public sealed record DadAutoPartyListingPublication(
    DadAutoPartySharePolicy StandingPolicy,
    IReadOnlyList<DadAutoPartyListing> Listings);

internal static class DadAutoPartyListingPublicationRules
{
    private static readonly TimeSpan ListingLifetime = TimeSpan.FromMinutes(15);

    public static DadAutoPartyListingPublication Build(
        DadAutoPartyConfiguration autoParty,
        IEnumerable<DadAutoPartyCrewCandidate>? crew,
        IEnumerable<DadPlannerGroup>? plans,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(autoParty);
        utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var policy = ResolveStandingPolicy(autoParty);
        var activities = BuildPermittedActivities(plans).ToList();
        var listings = (crew ?? [])
            .Where(static candidate => candidate != null && candidate.PermittedCombatJobIds.Count > 0)
            .Select(candidate => BuildListing(autoParty, policy, activities, candidate, utcNow))
            .Take(256)
            .ToList();
        return new(policy, listings);
    }

    private static DadAutoPartySharePolicy ResolveStandingPolicy(
        DadAutoPartyConfiguration configuration)
    {
        var policy = configuration.StandingSharePolicy?.Clone().Normalize();
        return policy is
        {
            IsValid: true,
            Mode: DadAutoPartyCharacterShareMode.CharacterList,
        }
            ? policy
            : new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                Enabled = false,
                Revision = Math.Max(1, configuration.StateGeneration),
            };
    }

    private static DadAutoPartyListing BuildListing(
        DadAutoPartyConfiguration configuration,
        DadAutoPartySharePolicy policy,
        IReadOnlyList<string> activities,
        DadAutoPartyCrewCandidate candidate,
        DateTime utcNow)
    {
        var projection = ProjectOpaqueIdentity(candidate.Identity.OpaqueCharacterId);
        return new DadAutoPartyListing
        {
            ListingId = projection.ListingId,
            OwnerId = configuration.RegisteredOwnerId,
            SharingIslandId = configuration.RegisteredIslandId,
            EffectiveShareMode = policy.Mode,
            OpaqueCharacterId = candidate.Identity.OpaqueCharacterId,
            DisplayLabel = projection.DisplayLabel,
            AllowedJobIds = candidate.PermittedCombatJobIds
                .Select(static jobId => jobId.ToString(CultureInfo.InvariantCulture))
                .ToList(),
            AllowedActivityIds = [.. activities],
            Available = candidate.Available,
            Revision = Math.Max(1, configuration.StateGeneration),
            ExpiresAtUtc = utcNow + ListingLifetime,
        }.Normalize();
    }

    private static IReadOnlyCollection<string> BuildPermittedActivities(IEnumerable<DadPlannerGroup>? plans)
    {
        var activities = new HashSet<string>(StringComparer.Ordinal)
        {
            DadAutoPartyFreeformRules.FormationActivityId,
        };
        foreach (var plan in (plans ?? []).Where(static plan => plan != null && !plan.IsTemplate))
        {
            var module = ResolveModule(plan.ActivityMode);
            var identity = ResolveActivityIdentity(plan, module);
            activities.Add($"dad-{module.ToString().ToLowerInvariant()}-{identity}");
            if (activities.Count >= 64)
                break;
        }
        return activities.Order(StringComparer.Ordinal).ToArray();
    }

    private static DadModuleId ResolveModule(DadPlannerActivityMode mode)
        => mode switch
        {
            DadPlannerActivityMode.Msq => DadModuleId.Msq,
            DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.DutySupportLeveling =>
                DadModuleId.DutySupport,
            DadPlannerActivityMode.Trust or DadPlannerActivityMode.TrustLeveling => DadModuleId.Trust,
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade => DadModuleId.PremadeDuty,
            DadPlannerActivityMode.DailyRoulette => DadModuleId.DailyMsq,
            DadPlannerActivityMode.Blunderville => DadModuleId.Blunderville,
            DadPlannerActivityMode.Mogtome => DadModuleId.Mogtome,
            DadPlannerActivityMode.Commendation => DadModuleId.Commendation,
            DadPlannerActivityMode.Astrope => DadModuleId.Astrope,
            DadPlannerActivityMode.LocalDuty => DadModuleId.Duty,
            DadPlannerActivityMode.CustomDuty => DadModuleId.CustomDuty,
            DadPlannerActivityMode.Squadron => DadModuleId.Squadron,
            DadPlannerActivityMode.VariantVvd => DadModuleId.VariantVvd,
            _ => DadModuleId.None,
        };

    private static uint ResolveActivityIdentity(DadPlannerGroup plan, DadModuleId module)
    {
        if (plan.DutyContentFinderConditionId != 0)
            return plan.DutyContentFinderConditionId;
        if (plan.RouletteTarget?.ContentFinderConditionId > 0)
            return plan.RouletteTarget.ContentFinderConditionId;
        if (plan.RouletteTarget?.RouletteId > 0)
            return plan.RouletteTarget.RouletteId;
        return (uint)(module switch
        {
            DadModuleId.PremadeDuty => Math.Clamp(plan.DutyExpectedPartySize, 2, 8),
            DadModuleId.DailyMsq or DadModuleId.Commendation or DadModuleId.Astrope => 4,
            DadModuleId.VariantVvd => Math.Clamp(plan.DutyExpectedPartySize, 1, 4),
            _ => Math.Clamp(plan.DutyExpectedPartySize, 1, 8),
        });
    }

    private static (string ListingId, string DisplayLabel) ProjectOpaqueIdentity(string opaqueCharacterId)
    {
        var bytes = Encoding.UTF8.GetBytes(opaqueCharacterId);
        var hash = SHA256.HashData(bytes);
        try
        {
            return (
                new Guid(hash.AsSpan(0, 16)).ToString("D"),
                $"Shared character {Convert.ToHexString(hash.AsSpan(16, 4)).ToLowerInvariant()}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}
