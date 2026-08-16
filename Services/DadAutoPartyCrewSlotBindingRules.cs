using System.Globalization;
using dad.Models;

namespace dad.Services;

/// <summary>
/// Keeps a saved Crew row and its registered-island route in lockstep. The binding key belongs to
/// the row's existing shared-identity placeholder, so changing one row never removes another row's route.
/// </summary>
internal static class DadAutoPartyCrewSlotBindingRules
{
    public static bool TryBind(
        DadAutoPartyConfiguration configuration,
        DadPlannerGroupSlot slot,
        DadAutoPartyPairing pairing,
        DadAutoPartyListing listing,
        uint requestedJobId,
        out string blocker)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(listing);
        blocker = string.Empty;

        if (!pairing.IsActive || listing.ExpiresAtUtc <= DateTime.UtcNow || !listing.Available ||
            !string.Equals(pairing.IslandId, listing.SharingIslandId, StringComparison.Ordinal) ||
            !string.Equals(pairing.OwnerId, listing.OwnerId, StringComparison.Ordinal))
        {
            return Fail("The selected shared Crew listing is no longer authorized.", out blocker);
        }
        if (!DadRosterCharacterMerge.IsCombatJob(requestedJobId) ||
            !listing.AllowedJobIds.Contains(
                requestedJobId.ToString(CultureInfo.InvariantCulture),
                StringComparer.Ordinal))
        {
            return Fail("Select one advertised combat job for the shared Crew slot.", out blocker);
        }

        var existingBindingId = slot.SharedIdentity?.BindingId?.Trim() ?? string.Empty;
        var duplicate = (configuration.RemoteBindings ?? []).Any(binding =>
            binding is { IsValid: true } &&
            string.Equals(binding.OpaqueCharacterId, listing.OpaqueCharacterId, StringComparison.Ordinal) &&
            !string.Equals(binding.FleetRowId, existingBindingId, StringComparison.Ordinal));
        if (duplicate)
            return Fail("That shared character is already bound to another Crew slot.", out blocker);

        var bindingId = existingBindingId.Length == 0
            ? "crew-slot-" + Guid.NewGuid().ToString("N")
            : existingBindingId;
        configuration.RemoteBindings ??= [];
        configuration.RemoteBindings.RemoveAll(binding => string.Equals(
            binding.FleetRowId,
            bindingId,
            StringComparison.Ordinal));
        configuration.RemoteBindings.Add(new DadAutoPartyRemoteBinding
        {
            FleetRowId = bindingId,
            OpaqueCharacterId = listing.OpaqueCharacterId,
            OwnerId = listing.OwnerId,
            IslandId = listing.SharingIslandId,
            RequestedJobId = requestedJobId.ToString(CultureInfo.InvariantCulture),
            OwnsQueueAuthority = DadPlannerSlotRules.IsLeaderSlot(slot.SlotId),
            OwnerConsentConfirmed = true,
        });
        slot.RequiredAccountKey = new DadAccountKey(string.Empty);
        slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
        slot.RequiredJobId = requestedJobId;
        slot.SharedIdentity = new DadSharedIdentityPlaceholder
        {
            IdentityToken = listing.OpaqueCharacterId,
            AccountToken = pairing.IslandId,
            BindingId = bindingId,
            CharacterLabel = listing.DisplayLabel,
            RequiresCharacter = true,
        };
        return true;
    }

    public static bool Clear(DadAutoPartyConfiguration configuration, DadPlannerGroupSlot slot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(slot);
        var bindingId = slot.SharedIdentity?.BindingId?.Trim() ?? string.Empty;
        if (bindingId.Length == 0)
            return false;
        return (configuration.RemoteBindings ?? []).RemoveAll(binding => string.Equals(
            binding.FleetRowId,
            bindingId,
            StringComparison.Ordinal)) > 0;
    }

    private static bool Fail(string message, out string blocker)
    {
        blocker = message;
        return false;
    }
}
