using AethertekUI;
using AethertekUI.Dalamud;
using System.Numerics;
using dad.Models;
using dad.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace dad.Windows;

public sealed class DadShoppingWizardWindow : Window
{
    private readonly MaterialWindowMotion motion = new();
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
    private string DestinationName => kind == DadShoppingAssociationOwnerKind.Plan ? Plan?.DisplayName ?? UiText.T("Unavailable preset") : Schedule?.DisplayName ?? UiText.T("Unavailable schedule");
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

    public override void PreDraw() => motion.Prepare(this, reducedMotion: false, roundedCorners: true);

    public override void PostDraw() => motion.Restore(this);

    public override void Draw()
    {
        motion.DrawChrome();
        UiGui.Title(WindowName.Split("##",2)[0]);
        MaterialText.Text(UiText.F("Step {0} of 4 — {1}", step + 1, UiText.T(Steps[step])));
        UiGui.TextWrapped("Choose an ADS list for a saved preset or schedule. Only Save shopping list commits changes. This wizard never starts a run or purchases items.");
        ImGui.Separator();
        if (saved)
        {
            MaterialText.TextWrapped(status);
            if (UiGui.Button("Close"))
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
            MaterialText.TextWrapped(status);
        ImGui.Separator();
        if (UiGui.Button("Cancel"))
            IsOpen = false;
        ImGui.SameLine();
        ImGui.BeginDisabled(step == 0);
        if (UiGui.Button("Back"))
        {
            state.Back();
            status = string.Empty;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (step < 3)
        {
            if (UiGui.Button("Next") && ValidateStep(out status))
                state.Next();
        }
        else if (UiGui.Button("Save shopping list"))
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
            status = UiText.T(status);
            if (saved)
                status = UiText.F("Shopping list saved and persisted for {0}.", DestinationName);
        }
    }

    private void DrawDestination()
    {
        var selectedKind = (int)kind;
        if (UiGui.Combo("Destination type", ref selectedKind, new[] { "Saved preset", "Schedule" }, 2))
            SelectDestination((DadShoppingAssociationOwnerKind)selectedKind, string.Empty);
        if (BeginRawCombo("Destination", string.IsNullOrEmpty(destinationId) ? UiText.T("Choose a destination") : DestinationName))
        {
            var destinations = kind == DadShoppingAssociationOwnerKind.Plan
                ? plugin.Configuration.PlannerGroups.Where(value => !value.IsTemplate).Select(value => (Id: value.GroupId, Name: value.DisplayName))
                : plugin.Configuration.Schedules.Select(value => (Id: value.ScheduleId, Name: value.DisplayName));
            foreach (var item in destinations)
                if (RawSelectable($"{item.Name}##{item.Id}", item.Id == destinationId))
                    SelectDestination(kind, item.Id);
            UiGui.EndCombo();
        }
    }

    private void DrawList()
    {
        if (UiGui.Button("Refresh ADS lists"))
            RefreshCatalog();
        UiGui.TextWrapped(catalogStatus);
        UiGui.InputText("Search lists", ref search, 160);
        MaterialText.TextWrapped(UiText.F("Selected: {0}", SelectedList?.Name ?? UiText.T(string.IsNullOrEmpty(draft.PresetId) ? "None" : "Unavailable — refresh or select another list")));
        foreach (var list in catalog?.Presets.Where(value => value.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            if (RawSelectable($"{list.Name}##{list.PresetId}", list.PresetId == draft.PresetId))
            {
                if (draft.PresetId != list.PresetId)
                    draft.ResetCompletionState();
                draft.PresetId = list.PresetId;
                draft.PresetName = list.Name;
            }
        }
        UiGui.TextWrapped("Targeted refill maintains current ownership thresholds. Spend until currency/capacity follows repeatable-row rules. Fill order over multiple runs remembers credited quantities until all targets are met.");
    }

    private void DrawShopper()
    {
        var slots = EligibleShoppers();
        var selected = slots.FirstOrDefault(MatchesShopper);
        if (BeginRawCombo("Exact primary character", selected == null ? UiText.T("Choose an eligible shopper") : ShopperLabel(selected)))
        {
            foreach (var slot in slots)
            {
                if (!RawSelectable($"{ShopperLabel(slot)}##{slot.SlotId}", MatchesShopper(slot)))
                    continue;
                if (!MatchesShopper(slot))
                    draft.ResetCompletionState();
                draft.ShopperSlotId = slot.SlotId;
                draft.ShopperAccountKey = slot.RequiredAccountKey;
                draft.ShopperCharacterKey = slot.RequiredCharacterKey;
            }
            UiGui.EndCombo();
        }
        if (slots.Count == 0)
            UiGui.TextWrapped("No eligible exact primary shopper. A schedule requires the same slot, account and character in every referenced Plan.");
        if (UiGui.CollapsingHeader("Optional delivery and post-command"))
        {
            var delivery = draft.RunAutoRetainerDelivery;
            if (UiGui.Checkbox("Run AutoRetainer delivery", ref delivery))
                draft.RunAutoRetainerDelivery = delivery;
            var command = draft.CustomCommand;
            if (UiGui.InputText("Post-command", ref command, 500))
                draft.CustomCommand = command;
            UiGui.TextWrapped("For finite orders, these actions wait until every target is fulfilled.");
        }
    }

    private void DrawReview()
    {
        MaterialText.TextWrapped(UiText.F("Destination: {0}", DestinationName));
        MaterialText.TextWrapped(UiText.F("Shopping list: {0}", SelectedList?.Name ?? UiText.T("Unavailable")));
        MaterialText.TextWrapped(UiText.F("Purchase type: {0}", UiText.T(PurchaseType(SelectedList?.Mode))));
        var shopper = EligibleShoppers().FirstOrDefault(MatchesShopper);
        MaterialText.TextWrapped(UiText.F("Shopper: {0}", shopper == null ? UiText.T("Unavailable or changed — choose again") : ShopperLabel(shopper)));
        MaterialText.TextWrapped(UiText.F("AutoRetainer delivery: {0}", UiText.T(draft.RunAutoRetainerDelivery ? "Yes" : "No")));
        MaterialText.TextWrapped(UiText.F("Post-command: {0}", string.IsNullOrEmpty(draft.CustomCommand) ? UiText.T("None") : draft.CustomCommand));
        if (UiGui.Button("Preview only — current client's character"))
        {
            var result = plugin.DutySupportAdsService.PreviewShopListPreset(draft);
            status = UiText.T(result.Summary) + UiText.T(" No purchase or run was started.");
        }
    }

    // Model values bypass translation while retaining UiGui's field sizing and original native ID root.
    private static bool BeginRawCombo(string label, string preview)
    {
        var requested = ImGui.CalcItemWidth();
        var padding = ImGui.GetStyle().FramePadding.X;
        var minimum = Math.Max(MaterialText.Measure(preview).X + 2 * padding + ImGui.GetFrameHeight(),
            Math.Max(80 * MaterialTheme.Metrics.Scale, MaterialText.Measure("00000000").X + 2 * padding));
        UiGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(requested, MathF.Ceiling(minimum)));
        ImGuiP.PushOverrideID(ImGui.GetID(label));
        try
        {
            var open = MaterialText.BeginCombo("", preview);
            if (!open) ImGui.PopID();
            return open;
        }
        catch { ImGui.PopID(); throw; }
    }

    private static bool RawSelectable(string label, bool selected)
        => MaterialText.Selectable(label, selected, ImGuiSelectableFlags.None,
            new Vector2(Math.Max(ImGui.GetContentRegionAvail().X, MaterialText.Measure(label.Split("##", 2)[0]).X), 0));

    private bool ValidateStep(out string error)
    {
        var valid = state.Validate(kind == DadShoppingAssociationOwnerKind.Plan ? Plan != null && !Plan.IsTemplate : Schedule != null,
            catalog?.Presets ?? [], EligibleShoppers(), out error);
        error = UiText.T(error);
        return valid;
    }

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
