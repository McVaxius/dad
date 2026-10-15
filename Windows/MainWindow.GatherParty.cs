using AethertekUI;
using Dalamud.Bindings.ImGui;
using dad.Models;
using dad.Services;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace dad.Windows;

public sealed partial class MainWindow
{
    private string gatherImportText = string.Empty;
    private string gatherImportStatus = string.Empty;
    private string gatherItemSearch = string.Empty;
    private string gatherLastSearch = "\0";
    private (uint Id, string Name)[]? gatherItemCatalog;
    private (uint Id, string Name)[] gatherSearchResults = [];

    private void DrawGatherPartyEditor(DadPresetPlannerOptions options, DadActivityPreset crew, DadRunResult run)
    {
        DrawSectionHeader("Gathering list", "Desired quantities are combined carried-stock targets. Other storage does not count.");
        var task = options.GatherParty;
        if (!plugin.IsGatherPreviewFor(task, crew.SelectedCharacters)) plugin.ClearGatherPreview();
        if (UiGui.CollapsingHeader("Import GBR or Teamcraft"))
        {
            UiGui.InputTextMultiline("##gather-export", ref gatherImportText, 1048576, new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 4));
            if (DadUi.Button("Import gathering list", DadUiTone.Accent))
            {
                EnsureGatherCatalog();
                var imported = DadGatherListImport.Parse(gatherImportText, name =>
                {
                    var matches = gatherItemCatalog!.Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                    return matches.Length == 1 ? matches[0].Id : 0;
                });
                if (imported.Task.Items.Count > 0)
                {
                    options.GatherParty = task = imported.Task;
                    plugin.ClearGatherPreview();
                    plugin.SavePlannerOptions();
                }
                gatherImportStatus = imported.Succeeded ? "Gathering list imported. Review targets before starting." :
                    string.Join(" | ", imported.Issues.Select(issue => issue.Message));
            }
            if (!string.IsNullOrEmpty(gatherImportStatus)) UiGui.TextWrapped(gatherImportStatus);
        }

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 18);
        if (UiGui.BeginCombo("Add material", "Choose material"))
        {
            DrawGatherChoices(task, Guid.Empty, (id, name) =>
            {
                task.Items.Add(new() { ItemId = id, Name = name });
                plugin.ClearGatherPreview();
                plugin.SavePlannerOptions();
            });
            UiGui.EndCombo();
        }
        ImGui.SameLine();
        if (DadUi.Button("Clear list"))
        {
            task.Items.Clear(); task.Allocations.Clear();
            plugin.ClearGatherPreview(); plugin.SavePlannerOptions();
        }
        if (DadUi.Button("Refresh carried stock", DadUiTone.Accent)) plugin.RefreshGatherPreview(task, crew.SelectedCharacters);
        ImGui.SameLine();
        if (DadUi.Button("Even split"))
        {
            task.Allocations.Clear();
            plugin.RecalculateGatherPreview(task); plugin.SavePlannerOptions();
        }
        UiGui.TextWrapped(plugin.GatherPreviewStatus);
        if (task.Items.Count == 0) return;

        var clients = crew.SelectedCharacters;
        var preview = plugin.GatherPreview;
        var live = !run.IsTerminal && run.ModuleId == DadModuleId.GatherParty && run.Request?.GatherParty != null;
        using var controls = MaterialControls.Push(MaterialControlContext.Dense);
        using var tight = MaterialTable.PushTightRows();
        var headerHeight = new[] { "Item", "Target", "On hand", "Remaining" }
            .Concat(clients.Select(client => client.SlotId))
            .Max(label => MaterialText.Measure(UiText.T(label)).Y);
        var height = MathF.Ceiling(Math.Max(ImGui.GetTextLineHeight(), headerHeight) +
            Math.Min(task.Items.Count, 10) * ImGui.GetFrameHeight() + ImGui.GetStyle().ScrollbarSize);
        if (!ImGui.BeginTable("dad-gather-items", 5 + clients.Count,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                new Vector2(-1, height))) return;
        // Scrolling tables own a child window; capture its cell-field scope.
        using var cells = UiGui.PushCellFields();
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 17);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 6);
        ImGui.TableSetupColumn("On hand", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5);
        ImGui.TableSetupColumn("Remaining", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5);
        foreach (var client in clients) ImGui.TableSetupColumn(client.SlotId, ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 6);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 30 * MaterialTheme.Metrics.Scale);
        ImGui.TableSetupScrollFreeze(1, 1);
        UiGui.TableHeadersRow();
        var remove = -1;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(task.Items.Count);
        while (clipper.Step())
        for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
        {
            var item = task.Items[index];
            var row = preview?.Items.FirstOrDefault(value => value.RowId == item.RowId);
            ImGui.PushID(item.RowId.ToString("N"));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var enabled = item.Enabled;
            if (UiGui.Checkbox("##enabled", ref enabled))
            {
                var before = DadIpcJson.Serialize(task);
                item.Enabled = enabled;
                task.Allocations.RemoveAll(allocation => allocation.ItemId == item.ItemId);
                GatherEdited(task, before, invalidate: true);
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            var name = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(item.ItemId, out var gameItem) ? gameItem.Name.ToString() : item.Name;
            if (UiGui.BeginCombo("##material", string.IsNullOrWhiteSpace(name) ? item.ItemId.ToString() : name))
            {
                DrawGatherChoices(task, item.RowId, (id, label) =>
                {
                    var before = DadIpcJson.Serialize(task);
                    task.Allocations.RemoveAll(allocation => allocation.ItemId == item.ItemId);
                    item.ItemId = id; item.Name = label; item.PreferredLocationId = 0;
                    GatherEdited(task, before, invalidate: true);
                });
                UiGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) UiGui.SetTooltip($"Item ID: {item.ItemId}; preferred gathering location: {item.PreferredLocationId}");
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var target = item.TargetQuantity;
            if (UiGui.InputInt("##target", ref target))
            {
                var before = DadIpcJson.Serialize(task);
                item.TargetQuantity = Math.Clamp(target, 1, DadGatherPartyPlanningRules.MaximumQuantity);
                GatherEdited(task, before);
            }
            ImGui.TableNextColumn();
            long? onHand = live && run.CurrentExecutorStatus.GatherCounts.TryGetValue(item.ItemId, out var current) ? current : row?.OnHand;
            MaterialText.Text(onHand?.ToString(UiText.Current.Culture) ?? "-");
            ImGui.TableNextColumn();
            MaterialText.Text(onHand.HasValue ? Math.Max(0, item.TargetQuantity - onHand.Value).ToString(UiText.Current.Culture) : "-");
            foreach (var client in clients)
            {
                ImGui.TableNextColumn();
                var order = live ? run.Request!.GatherOrders.FirstOrDefault(value => value.ItemId == item.ItemId && value.SlotId == client.SlotId) :
                    row?.Orders.FirstOrDefault(value => value.SlotId == client.SlotId);
                if (order == null) { UiGui.TextDisabled("-"); continue; }
                var quantity = order.Quantity;
                ImGui.SetNextItemWidth(-1);
                if (UiGui.InputInt($"##share-{client.SlotId}", ref quantity))
                {
                    var before = DadIpcJson.Serialize(task);
                    foreach (var value in row!.Orders)
                        if (!task.Allocations.Any(allocation => allocation.ItemId == item.ItemId && allocation.SlotId == value.SlotId))
                            task.Allocations.Add(new() { ItemId = item.ItemId, SlotId = value.SlotId, Quantity = value.Quantity });
                    task.Allocations.Single(allocation => allocation.ItemId == item.ItemId && allocation.SlotId == client.SlotId).Quantity =
                        Math.Clamp(quantity, 0, DadGatherPartyPlanningRules.MaximumQuantity);
                    GatherEdited(task, before);
                }
                if (ImGui.IsItemHovered()) UiGui.SetTooltip($"Carried stock: {order.StartingCount}; native target: {order.NativeTarget}");
            }
            ImGui.TableNextColumn();
            if (UiGui.IconButton("##remove", MaterialIcon.Close, display: string.Empty)) remove = index;
            if (ImGui.IsItemHovered()) UiGui.SetTooltip("Remove material");
            ImGui.PopID();
        }
        clipper.End(); clipper.Destroy();
        ImGui.EndTable();
        if (remove >= 0)
        {
            var id = task.Items[remove].ItemId;
            task.Items.RemoveAt(remove);
            if (!task.Items.Any(item => item.ItemId == id)) task.Allocations.RemoveAll(allocation => allocation.ItemId == id);
            plugin.ClearGatherPreview(); plugin.SavePlannerOptions();
        }
    }

    private void GatherEdited(DadGatherPartyTask task, string before, bool invalidate = false)
    {
        if (invalidate) plugin.ClearGatherPreview(); else plugin.RecalculateGatherPreview(task);
        plugin.QueueDebouncedPlannerOptionsSave("gathering-list", before, () => DadIpcJson.Serialize(plugin.PlannerOptions.GatherParty));
    }

    private void EnsureGatherCatalog()
    {
        if (gatherItemCatalog != null) return;
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        gatherItemCatalog = Plugin.DataManager.GetExcelSheet<GatheringItem>().Where(row => row.Item.RowId != 0)
            .Select(row => (Id: row.Item.RowId, Name: items.TryGetRow(row.Item.RowId, out var item) ? item.Name.ToString() : row.Item.RowId.ToString())).DistinctBy(item => item.Id)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void DrawGatherChoices(DadGatherPartyTask task, Guid editingRow, Action<uint, string> select)
    {
        EnsureGatherCatalog();
        UiGui.InputText("Find gathering item", ref gatherItemSearch, 128);
        if (gatherLastSearch != gatherItemSearch)
        {
            gatherLastSearch = gatherItemSearch;
            gatherSearchResults = gatherItemCatalog!.Where(item => string.IsNullOrWhiteSpace(gatherItemSearch) ||
                item.Name.Contains(gatherItemSearch, StringComparison.CurrentCultureIgnoreCase) || item.Id.ToString() == gatherItemSearch).Take(80).ToArray();
        }
        foreach (var item in gatherSearchResults)
        {
            ImGui.BeginDisabled(task.Items.Any(row => row.ItemId == item.Id && row.RowId != editingRow));
            if (UiGui.Selectable($"{item.Name}##gather-choice-{item.Id}")) select(item.Id, item.Name);
            ImGui.EndDisabled();
        }
    }
}
