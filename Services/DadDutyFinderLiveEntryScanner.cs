using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

internal static unsafe class DadDutyFinderLiveEntryScanner
{
    public static bool TryCapture(
        AgentContentsFinder* agent,
        AtkUnitBase* addonBase,
        out DadDutyFinderListSnapshot snapshot,
        out string reason)
    {
        snapshot = new DadDutyFinderListSnapshot();
        reason = string.Empty;
        if (agent == null)
        {
            reason = "AgentContentsFinder is unavailable.";
            return false;
        }

        if (addonBase == null || !addonBase->IsVisible)
        {
            reason = "ContentsFinder addon is unavailable or hidden.";
            return false;
        }

        var addon = (AddonContentsFinder*)addonBase;
        var dutyList = addon->DutyList;
        if (dutyList == null)
        {
            reason = "ContentsFinder DutyList is unavailable.";
            return false;
        }

        try
        {
            var before = CaptureMarkers(agent, addon, dutyList);
            if (before.ListChanged)
            {
                snapshot = CreateSnapshot(
                    agent,
                    addonBase,
                    dutyList,
                    before,
                    listChanged: true,
                    [],
                    []);
                return true;
            }

            if (!TryReadContentEntries(agent, before.ContentEntryCount, out var contentEntries, out reason) ||
                !TryReadTreeItems(dutyList, before.TreeItemCount, out var treeItems, out reason))
            {
                return false;
            }

            var afterFirstPass = CaptureMarkers(agent, addon, dutyList);
            if (!IsSameListGeneration(before, afterFirstPass))
            {
                snapshot = CreateSnapshot(agent, addonBase, dutyList, before, listChanged: true, [], []);
                return true;
            }

            // Validate the whole native backing data a second time inside this
            // capture. The cross-pulse stable-mapping gate still requires two
            // complete identical snapshots after this internal consistency
            // check succeeds.
            if (!TryReadContentEntries(agent, before.ContentEntryCount, out var verificationEntries, out reason) ||
                !TryReadTreeItems(dutyList, before.TreeItemCount, out var verificationTreeItems, out reason))
            {
                return false;
            }

            var afterSecondPass = CaptureMarkers(agent, addon, dutyList);
            var changedDuringCapture =
                !IsSameListGeneration(before, afterSecondPass) ||
                !contentEntries.SequenceEqual(verificationEntries) ||
                !HaveSameTreeAuthority(treeItems, verificationTreeItems);

            snapshot = CreateSnapshot(
                agent,
                addonBase,
                dutyList,
                before,
                changedDuringCapture,
                changedDuringCapture ? [] : contentEntries,
                changedDuringCapture ? [] : treeItems);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Duty Finder live-entry scan failed: {ex.Message}";
            snapshot = new DadDutyFinderListSnapshot();
            return false;
        }
    }

    private static DadDutyFinderListSnapshot CreateSnapshot(
        AgentContentsFinder* agent,
        AtkUnitBase* addonBase,
        AtkComponentTreeList* dutyList,
        DadDutyFinderCaptureMarkers markers,
        bool listChanged,
        IReadOnlyList<DadDutyFinderContentEntry> contentEntries,
        IReadOnlyList<DadDutyFinderTreeItem> treeItems)
        => new()
        {
            CharacterContentId = markers.CharacterContentId,
            AddonIdentity = (nuint)addonBase,
            AgentIdentity = (nuint)agent,
            DutyListIdentity = (nuint)dutyList,
            ContentListStorageIdentity = markers.ContentStorageIdentity,
            DutyListStorageIdentity = markers.TreeStorageIdentity,
            SelectedTab = markers.SelectedTab,
            SelectedRadioButton = markers.SelectedRadioButton,
            DeclaredEntryCount = markers.DeclaredEntryCount,
            TreeItemCount = markers.TreeItemCount,
            ListChanged = listChanged,
            ContentEntries = contentEntries,
            TreeItems = treeItems,
        };

    private static DadDutyFinderCaptureMarkers CaptureMarkers(
        AgentContentsFinder* agent,
        AddonContentsFinder* addon,
        AtkComponentTreeList* dutyList)
        => new(
            Plugin.PlayerState.ContentId,
            (nuint)addon->DutyList,
            agent->SelectedTab,
            addon->SelectedRadioButton,
            addon->NumEntries,
            dutyList->Items.Count,
            agent->ContentList.Count,
            agent->ListChanged,
            (nuint)agent->ContentList.First,
            (nuint)dutyList->Items.First);

    private static bool IsSameListGeneration(
        DadDutyFinderCaptureMarkers expected,
        DadDutyFinderCaptureMarkers observed)
        => !expected.ListChanged &&
           !observed.ListChanged &&
           expected.CharacterContentId == observed.CharacterContentId &&
           expected.DutyListIdentity == observed.DutyListIdentity &&
           expected.SelectedTab == observed.SelectedTab &&
           expected.SelectedRadioButton == observed.SelectedRadioButton &&
           expected.DeclaredEntryCount == observed.DeclaredEntryCount &&
           expected.TreeItemCount == observed.TreeItemCount &&
           expected.ContentEntryCount == observed.ContentEntryCount &&
           expected.ContentStorageIdentity == observed.ContentStorageIdentity &&
           expected.TreeStorageIdentity == observed.TreeStorageIdentity;

    private static bool TryReadContentEntries(
        AgentContentsFinder* agent,
        int expectedCount,
        out List<DadDutyFinderContentEntry> entries,
        out string reason)
    {
        entries = new List<DadDutyFinderContentEntry>(expectedCount);
        reason = string.Empty;
        if (agent->ContentList.Count != expectedCount)
        {
            reason = "AgentContentsFinder content count changed during the live scan.";
            return false;
        }

        for (var index = 0; index < expectedCount; index++)
        {
            var content = agent->ContentList[index].Value;
            if (content == null)
            {
                reason = $"AgentContentsFinder content entry {index + 1} is null.";
                return false;
            }

            entries.Add(new DadDutyFinderContentEntry(
                index,
                ConvertContentType(content->Id.ContentType),
                content->Id.Id,
                ReadSeString(content->Name.StringPtr.Value)));
        }

        return true;
    }

    private static bool TryReadTreeItems(
        AtkComponentTreeList* dutyList,
        int expectedCount,
        out List<DadDutyFinderTreeItem> treeItems,
        out string reason)
    {
        treeItems = new List<DadDutyFinderTreeItem>(expectedCount);
        reason = string.Empty;
        if (dutyList->Items.Count != expectedCount)
        {
            reason = "DutyList tree count changed during the live scan.";
            return false;
        }

        for (var treeIndex = 0; treeIndex < expectedCount; treeIndex++)
        {
            var item = dutyList->GetItem(treeIndex);
            if (item == null || item->UIntValues.Count == 0)
            {
                reason = $"DutyList tree item {treeIndex} is null or has no item type.";
                return false;
            }

            var itemType = item->UIntValues[0];
            var isLeaf = itemType is
                (uint)TreeListItemType.None or
                (uint)TreeListItemType.LastItemInGroup;
            var itemLabel = item->StringValues.Count > 0
                ? ReadSeString(item->StringValues[0].Value)
                : string.Empty;
            var enabled = !isLeaf || !dutyList->GetItemDisabledState(treeIndex);
            treeItems.Add(new DadDutyFinderTreeItem(
                treeIndex,
                isLeaf,
                enabled,
                itemLabel,
                ReadRendererNodeTextDiagnostic(item)));
        }

        return true;
    }

    private static bool HaveSameTreeAuthority(
        IReadOnlyList<DadDutyFinderTreeItem> left,
        IReadOnlyList<DadDutyFinderTreeItem> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].TreeIndex != right[index].TreeIndex ||
                left[index].IsLeaf != right[index].IsLeaf ||
                left[index].Enabled != right[index].Enabled ||
                !string.Equals(left[index].ItemLabel, right[index].ItemLabel, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static DadDutyFinderLiveContentType ConvertContentType(ContentsType contentType)
        => contentType switch
        {
            ContentsType.Roulette => DadDutyFinderLiveContentType.Roulette,
            ContentsType.Regular => DadDutyFinderLiveContentType.Regular,
            _ => DadDutyFinderLiveContentType.None,
        };

    private static string ReadSeString(byte* value)
    {
        if (value == null)
            return string.Empty;

        try
        {
            return MemoryHelper.ReadSeStringNullTerminated((nint)value).TextValue.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadRendererNodeTextDiagnostic(AtkComponentTreeListItem* item)
    {
        // Renderer text is deliberately excluded from fingerprints and mapping.
        // It can be truncated, recycled, or absent for rows outside the viewport.
        try
        {
            var renderer = item->Renderer;
            var textNode = renderer == null ? null : renderer->GetTextNodeById(5);
            return textNode == null
                ? string.Empty
                : ReadSeString(textNode->NodeText.StringPtr.Value);
        }
        catch
        {
            return string.Empty;
        }
    }

    private readonly record struct DadDutyFinderCaptureMarkers(
        ulong CharacterContentId,
        nuint DutyListIdentity,
        byte SelectedTab,
        uint SelectedRadioButton,
        uint DeclaredEntryCount,
        int TreeItemCount,
        int ContentEntryCount,
        bool ListChanged,
        nuint ContentStorageIdentity,
        nuint TreeStorageIdentity);
}
