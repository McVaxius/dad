using System.Numerics;
using dad.Models;
using dad.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace dad.Windows;

public sealed class DadShoppingWizardWindow : Window
{
    private readonly Plugin plugin;
    private readonly DadShoppingWizardDraft state = new();
    private DadShoppingAssociationOwnerKind kind => state.DestinationKind;
    private string destinationId => state.DestinationId;
    private DadShoppingAssociation draft => state.Association;
    private DadAdsShopListPresetCatalog? catalog;
    private string search = string.Empty;
    private string status = string.Empty;
    private string catalogStatus = string.Empty;
    private int step => state.Step;
    private bool saved => state.Saved;
    private static readonly string[] Steps = ["Choose destination", "Choose shopping list", "Choose shopper", "Review and save"];

    public DadShoppingWizardWindow(Plugin plugin) : base("Shopping List Wizard###DadShoppingWizard")
    {
        this.plugin = plugin;
        Size = new Vector2(780, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(650, 460), MaximumSize = new(float.MaxValue) };
    }

    public void Open(DadShoppingAssociationOwnerKind? destinationKind, string? id)
    {
        search = string.Empty;
        status = string.Empty;
        SelectDestination(destinationKind ?? DadShoppingAssociationOwnerKind.Plan, id ?? string.Empty);
        RefreshCatalog();
        IsOpen = true;
    }

    public override void OnClose()
    {
        state.Cancel();
        catalog = null;
        status = string.Empty;
    }

    private DadPlannerGroup? Plan => plugin.Configuration.PlannerGroups.FirstOrDefault(value => value.GroupId == destinationId);
    private DadScheduleDefinition? Schedule => plugin.Configuration.Schedules.FirstOrDefault(value => value.ScheduleId == destinationId);
    private string DestinationName => kind == DadShoppingAssociationOwnerKind.Plan ? Plan?.DisplayName ?? "Unavailable preset" : Schedule?.DisplayName ?? "Unavailable schedule";
    private DadAdsShopListPresetSummary? SelectedList => catalog?.Presets.SingleOrDefault(value => value.PresetId == draft.PresetId);

    private void SelectDestination(DadShoppingAssociationOwnerKind destinationKind, string id)
    {
        var association = destinationKind == DadShoppingAssociationOwnerKind.Plan
            ? plugin.Configuration.PlannerGroups.FirstOrDefault(value => value.GroupId == id)?.ShoppingAssociation
            : plugin.Configuration.Schedules.FirstOrDefault(value => value.ScheduleId == id)?.ShoppingAssociation;
        state.SelectDestination(destinationKind, id, association);
        status = string.Empty;
    }

    private IReadOnlyList<DadPlannerGroupSlot> EligibleShoppers()
    {
        if (kind == DadShoppingAssociationOwnerKind.Schedule)
            return Schedule == null ? [] : DadShoppingAssociationRules.ResolveCommonScheduleShopperSlots(Schedule, plugin.Configuration.PlannerGroups);
        return Plan == null ? [] : DadPlannerSlotRules.NormalizeGroupSlots(Plan.Slots)
            .Where(slot => !slot.IsSubstitute && slot.SharedIdentity == null && !slot.RequiredAccountKey.IsEmpty && !slot.RequiredCharacterKey.IsEmpty).ToList();
    }

    private bool MatchesShopper(DadPlannerGroupSlot slot)
        => state.MatchesShopper(slot);

    private void RefreshCatalog()
    {
        var result = plugin.DutySupportAdsService.GetShopListPresets();
        catalog = result.Readable ? result.Catalog : null;
        catalogStatus = result.Summary;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"Step {step + 1} of 4 — {Steps[step]}");
        ImGui.TextWrapped("Choose an ADS list for a saved preset or schedule. Only Save shopping list commits changes. This wizard never starts a run or purchases items.");
        ImGui.Separator();
        if (saved)
        {
            ImGui.TextWrapped(status);
            if (ImGui.Button("Close"))
                IsOpen = false;
            return;
        }

        switch (step)
        {
            case 0: DrawDestination(); break;
            case 1: DrawList(); break;
            case 2: DrawShopper(); break;
            case 3: DrawReview(); break;
        }
        ImGui.Spacing();
        if (!string.IsNullOrEmpty(status))
            ImGui.TextWrapped(status);
        ImGui.Separator();
        if (ImGui.Button("Cancel"))
            IsOpen = false;
        ImGui.SameLine();
        ImGui.BeginDisabled(step == 0);
        if (ImGui.Button("Back"))
        {
            state.Back();
            status = string.Empty;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (step < 3)
        {
            if (ImGui.Button("Next") && ValidateStep(out status))
                state.Next();
        }
        else if (ImGui.Button("Save shopping list"))
        {
            RefreshCatalog();
            if (!ValidateStep(out status))
                return;
            state.TrySave(candidate =>
            {
                string error;
                var accepted = kind == DadShoppingAssociationOwnerKind.Plan
                    ? plugin.TrySavePlanShoppingAssociation(destinationId, candidate, out error)
                    : plugin.TrySaveScheduleShoppingAssociation(destinationId, candidate, out error);
                return (accepted, error);
            }, out status);
            if (saved)
                status = $"Shopping list saved and persisted for {DestinationName}.";
        }
    }

    private void DrawDestination()
    {
        var selectedKind = (int)kind;
        if (ImGui.Combo("Destination type", ref selectedKind, new[] { "Saved preset", "Schedule" }, 2))
            SelectDestination((DadShoppingAssociationOwnerKind)selectedKind, string.Empty);
        if (ImGui.BeginCombo("Destination", string.IsNullOrEmpty(destinationId) ? "Choose a destination" : DestinationName))
        {
            var destinations = kind == DadShoppingAssociationOwnerKind.Plan
                ? plugin.Configuration.PlannerGroups.Where(value => !value.IsTemplate).Select(value => (Id: value.GroupId, Name: value.DisplayName))
                : plugin.Configuration.Schedules.Select(value => (Id: value.ScheduleId, Name: value.DisplayName));
            foreach (var item in destinations)
                if (ImGui.Selectable($"{item.Name}##{item.Id}", item.Id == destinationId))
                    SelectDestination(kind, item.Id);
            ImGui.EndCombo();
        }
    }

    private void DrawList()
    {
        if (ImGui.Button("Refresh ADS lists"))
            RefreshCatalog();
        ImGui.TextWrapped(catalogStatus);
        ImGui.InputText("Search lists", ref search, 160);
        ImGui.TextWrapped($"Selected: {SelectedList?.Name ?? (string.IsNullOrEmpty(draft.PresetId) ? "None" : "Unavailable — refresh or select another list")}");
        foreach (var list in catalog?.Presets.Where(value => value.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            if (ImGui.Selectable($"{list.Name}##{list.PresetId}", list.PresetId == draft.PresetId))
            {
                if (draft.PresetId != list.PresetId)
                    draft.ResetCompletionState();
                draft.PresetId = list.PresetId;
                draft.PresetName = list.Name;
            }
        }
        ImGui.TextWrapped("Targeted refill maintains current ownership thresholds. Spend until currency/capacity follows repeatable-row rules. Fill order over multiple runs remembers credited quantities until all targets are met.");
    }

    private void DrawShopper()
    {
        var slots = EligibleShoppers();
        var selected = slots.FirstOrDefault(MatchesShopper);
        if (ImGui.BeginCombo("Exact primary character", selected == null ? "Choose an eligible shopper" : ShopperLabel(selected)))
        {
            foreach (var slot in slots)
            {
                if (!ImGui.Selectable($"{ShopperLabel(slot)}##{slot.SlotId}", MatchesShopper(slot)))
                    continue;
                if (!MatchesShopper(slot))
                    draft.ResetCompletionState();
                draft.ShopperSlotId = slot.SlotId;
                draft.ShopperAccountKey = slot.RequiredAccountKey;
                draft.ShopperCharacterKey = slot.RequiredCharacterKey;
            }
            ImGui.EndCombo();
        }
        if (slots.Count == 0)
            ImGui.TextWrapped("No eligible exact primary shopper. A schedule requires the same slot, account and character in every referenced Plan.");
        if (ImGui.CollapsingHeader("Optional delivery and post-command"))
        {
            var delivery = draft.RunAutoRetainerDelivery;
            if (ImGui.Checkbox("Run AutoRetainer delivery", ref delivery))
                draft.RunAutoRetainerDelivery = delivery;
            var command = draft.CustomCommand;
            if (ImGui.InputText("Post-command", ref command, 500))
                draft.CustomCommand = command;
            ImGui.TextWrapped("For finite orders, these actions wait until every target is fulfilled.");
        }
    }

    private void DrawReview()
    {
        ImGui.TextWrapped($"Destination: {DestinationName}");
        ImGui.TextWrapped($"Shopping list: {SelectedList?.Name ?? "Unavailable"}");
        ImGui.TextWrapped($"Purchase type: {PurchaseType(SelectedList?.Mode)}");
        var shopper = EligibleShoppers().FirstOrDefault(MatchesShopper);
        ImGui.TextWrapped($"Shopper: {(shopper == null ? "Unavailable or changed — choose again" : ShopperLabel(shopper))}");
        ImGui.TextWrapped($"AutoRetainer delivery: {(draft.RunAutoRetainerDelivery ? "Yes" : "No")}");
        ImGui.TextWrapped($"Post-command: {(string.IsNullOrEmpty(draft.CustomCommand) ? "None" : draft.CustomCommand)}");
        if (ImGui.Button("Preview only — current client's character"))
        {
            var result = plugin.DutySupportAdsService.PreviewShopListPreset(draft);
            status = result.Summary + " No purchase or run was started.";
        }
    }

    private bool ValidateStep(out string error)
        => state.Validate(kind == DadShoppingAssociationOwnerKind.Plan ? Plan != null && !Plan.IsTemplate : Schedule != null,
            catalog?.Presets ?? [], EligibleShoppers(), out error);

    private static string ShopperLabel(DadPlannerGroupSlot slot)
        => $"{slot.SlotId} | {slot.RequiredAccountKey.Value} | {slot.RequiredCharacterKey.Value}";

    private static string PurchaseType(string? mode) => mode switch
    {
        "targeted-refill" => "Targeted refill",
        "spend-until-currency-or-capacity" => "Spend until currency/capacity",
        "fill-order-over-multiple-runs" => "Fill order over multiple runs",
        _ => "Unavailable",
    };
}
