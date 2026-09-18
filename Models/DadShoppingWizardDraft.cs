using dad.Services;

namespace dad.Models;

// This draft is deliberately independent of configuration persistence and run dispatch.
public sealed class DadShoppingWizardDraft
{
    public DadShoppingAssociationOwnerKind DestinationKind { get; private set; }
    public string DestinationId { get; private set; } = string.Empty;
    public DadShoppingAssociation Association { get; private set; } = new();
    public int Step { get; private set; }
    public bool Saved { get; private set; }

    public void SelectDestination(DadShoppingAssociationOwnerKind kind, string id, DadShoppingAssociation? existing)
    {
        DestinationKind = kind;
        DestinationId = id;
        Association = existing?.Clone() ?? new();
        Step = 0;
        Saved = false;
    }

    public void Next() => Step = Math.Min(3, Step + 1);
    public void Back() => Step = Math.Max(0, Step - 1);
    public void Cancel() => SelectDestination(DadShoppingAssociationOwnerKind.Plan, string.Empty, null);

    public bool TrySave(Func<DadShoppingAssociation, (bool Succeeded, string Message)> save, out string message)
    {
        if (Step != 3 || Saved)
        {
            message = "Review this shopping draft before saving.";
            return false;
        }
        var result = save(Association.Clone());
        Saved = result.Succeeded;
        message = result.Message;
        return Saved;
    }

    public bool MatchesShopper(DadPlannerGroupSlot slot)
        => string.Equals(slot.SlotId, Association.ShopperSlotId, StringComparison.OrdinalIgnoreCase) &&
            DadRosterIdentity.SameAccount(slot.RequiredAccountKey, Association.ShopperAccountKey) &&
            string.Equals(slot.RequiredCharacterKey.Value, Association.ShopperCharacterKey.Value, StringComparison.OrdinalIgnoreCase);

    public bool Validate(bool destinationExists, IReadOnlyCollection<DadAdsShopListPresetSummary> lists,
        IReadOnlyCollection<DadPlannerGroupSlot> shoppers, out string error)
    {
        error = string.Empty;
        if (!destinationExists || string.IsNullOrEmpty(DestinationId))
            error = "Choose an available saved preset or schedule.";
        else if (Step >= 1 && lists.Count(list => list.PresetId == Association.PresetId) != 1)
            error = "Choose an available ADS list. Use Refresh ADS lists if needed.";
        else if (Step >= 2 && shoppers.Count(MatchesShopper) != 1)
            error = "The selected exact shopper is unavailable or changed. Choose the intended character again.";
        return error.Length == 0;
    }
}
