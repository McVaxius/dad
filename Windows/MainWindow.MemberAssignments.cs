using dad.Models;
using dad.Services;
using Dalamud.Bindings.ImGui;

namespace dad.Windows;

public sealed partial class MainWindow
{
    private string memberDraftOwner = string.Empty;
    private List<DadShoppingAssociation> memberDrafts = [];

    private void DrawMemberAssignmentsEditor(DadShoppingAssociationOwnerKind kind, string ownerId, string ownerName,
        DadShoppingAssociation? legacy, IReadOnlyList<DadPlannerGroupSlot> slots, bool ownerMutationLocked)
    {
        if (!DadUi.BeginCard($"member-actions-{kind}-{ownerId}")) return;
        DadUi.Heading("MEMBER SHOPPING & FINAL COMMANDS", "Optional ADS list and final slash command per member. Blank choices are off.");
        UiGui.TextWrapped("Final commands wait for the successful full round and shopping, including every schedule entry and repeat.");
        UiGui.TextWrapped("The last preset command applies; a nonblank schedule command overrides it.");
        UiGui.TextWrapped("Apply to all copies choices to displayed members. Remote clients require remote-command permission.");
        UiGui.TextWrapped("Acknowledgement confirms registered submission. Command effects remain unverified.");
        var ownerKey = $"{kind}:{ownerId}";
        if (memberDraftOwner != ownerKey)
        {
            memberDraftOwner = ownerKey;
            var source = kind == DadShoppingAssociationOwnerKind.Plan
                ? DadShoppingAssociationRules.PlanAssignments(plugin.Configuration.PlannerGroups.SingleOrDefault(value => value.GroupId == ownerId))
                : DadShoppingAssociationRules.ScheduleAssignments(plugin.Configuration.Schedules.SingleOrDefault(value => value.ScheduleId == ownerId));
            memberDrafts = source.Select(static value => value.Clone()).ToList();
            foreach (var slot in slots)
                if (!memberDrafts.Any(value => Matches(value, slot)))
                    memberDrafts.Add(DadShoppingAssociationRules.CopyChoicesToMember(new(), slot, null));
            shoppingEditorStatus = string.Empty;
        }
        if (UiGui.SmallButton("Refresh ADS presets"))
        {
            var result = plugin.DutySupportAdsService.GetShopListPresets();
            shoppingPresetCatalog = result.Readable ? result.Catalog : null;
            shoppingCatalogStatus = result.Summary;
        }
        UiGui.TextDisabled(shoppingCatalogStatus);
        var blocker = plugin.GetShareMutationBlocker();
        var locked = ownerMutationLocked || blocker.Length > 0;
        ImGui.BeginDisabled(locked);
        var presets = shoppingPresetCatalog?.Presets.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        for (var index = 0; index < memberDrafts.Count; index++)
        {
            var row = memberDrafts[index];
            var slot = slots.SingleOrDefault(value => Matches(row, value));
            ImGui.PushID(row.AssociationId);
            ImGui.Separator();
            UiGui.TextWrapped(slot == null ? UiText.T("Unavailable member") + $": {row.ShopperSlotId}" : FormatShoppingShopper(slot));
            if (slot == null)
            {
                UiGui.TextWrapped("This frozen member is no longer an exact eligible row. Remove the old assignment before saving a replacement.");
                if (UiGui.SmallButton("Remove member assignment")) { memberDrafts.RemoveAt(index--); }
                ImGui.PopID(); continue;
            }
            if (DrawMemberAssignmentFields(row, presets))
            {
                foreach (var target in slots)
                {
                    var targetIndex = memberDrafts.FindIndex(value => Matches(value, target));
                    if (targetIndex >= 0) memberDrafts[targetIndex] = DadShoppingAssociationRules.CopyChoicesToMember(row, target, memberDrafts[targetIndex]);
                }
            }
            var receipt = plugin.Configuration.RoundCompletions.SelectMany(value => value.Members).LastOrDefault(value =>
                DadRosterIdentity.SameAccount(value.Recipient.ManagedAccountKey, row.ShopperAccountKey) &&
                value.Recipient.ActiveCharacterKey.Value == row.ShopperCharacterKey.Value);
            if (receipt != null) UiGui.TextWrapped(receipt.Summary);
            ImGui.PopID();
        }
        if (slots.Count == 0) UiGui.TextWrapped("No exact eligible members are available for this owner.");
        if (UiGui.Button("Save member assignments"))
        {
            var saved = plugin.TrySaveMemberAssignments(kind, ownerId, memberDrafts, out var error);
            shoppingEditorStatus = saved ? "Member assignments saved." : error;
            if (saved) memberDraftOwner = string.Empty;
        }
        ImGui.SameLine();
        if (UiGui.Button("Discard edits")) memberDraftOwner = string.Empty;
        ImGui.EndDisabled();
        if (locked && blocker.Length > 0) UiGui.TextWrapped(blocker);
        if (shoppingEditorStatus.Length > 0) UiGui.TextWrapped(shoppingEditorStatus);
        DrawShoppingFailureHistory(kind, ownerId, locked, blocker);
        DadUi.EndCard();

        static bool Matches(DadShoppingAssociation row, DadPlannerGroupSlot slot)
            => string.Equals(row.ShopperSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase) &&
               DadRosterIdentity.SameAccount(row.ShopperAccountKey, slot.RequiredAccountKey) &&
               string.Equals(row.ShopperCharacterKey.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase);
    }
    internal static bool DrawMemberAssignmentFields(DadShoppingAssociation row, IReadOnlyList<DadAdsShopListPresetSummary> presets)
    {
        var selected = presets.FirstOrDefault(value => value.PresetId == row.PresetId);
        UiGui.TextWrapped("ADS preset");
        ImGui.SetNextItemWidth(-1);
        if (UiGui.BeginCombo("##MemberList", selected?.Name ?? (row.PresetId.Length > 0 ? row.PresetName : UiText.T("None"))))
        {
            if (UiGui.Selectable("None", row.PresetId.Length == 0))
            { row.PresetId = row.PresetName = string.Empty; row.RunAutoRetainerDelivery = false; }
            foreach (var preset in presets)
                if (UiGui.Selectable(preset.Name, preset.PresetId == row.PresetId))
                { row.PresetId = preset.PresetId; row.PresetName = preset.Name; }
            UiGui.EndCombo();
        }
        var command = row.CustomCommand;
        UiGui.TextWrapped("Final slash command");
        ImGui.SetNextItemWidth(-1);
        if (UiGui.InputText("##MemberFinalCommand", ref command, 512)) row.CustomCommand = command;
        var delivery = row.RunAutoRetainerDelivery;
        ImGui.BeginDisabled(row.PresetId.Length == 0);
        if (UiGui.Checkbox("AutoRetainer delivery after fulfillment", ref delivery)) row.RunAutoRetainerDelivery = delivery;
        ImGui.EndDisabled();
        return UiGui.SmallButton("Apply this row to all members");
    }

}
