using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace dad.Services;

internal enum DadDutyFinderLiveContentType
{
    None,
    Roulette,
    Regular,
}

internal readonly record struct DadDutyFinderLiveTarget(
    DadDutyFinderLiveContentType ContentType,
    uint RowId);

internal sealed record DadDutyFinderContentEntry(
    int ContentListIndex,
    DadDutyFinderLiveContentType ContentType,
    uint RowId,
    string LocalizedName);

internal sealed record DadDutyFinderTreeItem(
    int TreeIndex,
    bool IsLeaf,
    bool Enabled,
    string ItemLabel,
    string RendererNodeText = "");

internal sealed record DadDutyFinderUiRow(
    int TreeIndex,
    int CallbackOrdinal,
    bool Enabled,
    string ItemLabel,
    string RendererNodeText);

internal sealed class DadDutyFinderListSnapshot
{
    public ulong CharacterContentId { get; init; }
    public nuint AddonIdentity { get; init; }
    public nuint AgentIdentity { get; init; }
    public nuint DutyListIdentity { get; init; }
    public nuint ContentListStorageIdentity { get; init; }
    public nuint DutyListStorageIdentity { get; init; }
    public byte SelectedTab { get; init; }
    public uint SelectedRadioButton { get; init; }
    public uint DeclaredEntryCount { get; init; }
    public int TreeItemCount { get; init; }
    public bool ListChanged { get; init; }
    public IReadOnlyList<DadDutyFinderContentEntry> ContentEntries { get; init; } = [];
    public IReadOnlyList<DadDutyFinderTreeItem> TreeItems { get; init; } = [];

    public IReadOnlyList<DadDutyFinderUiRow> BuildUiRows()
    {
        var rows = new List<DadDutyFinderUiRow>();
        var callbackOrdinal = 0;
        foreach (var item in TreeItems)
        {
            if (!item.IsLeaf)
                continue;

            // Callback 3 uses the one-based leaf ordinal. Disabled leaves still
            // occupy an ordinal; only headers are excluded.
            callbackOrdinal++;
            rows.Add(new DadDutyFinderUiRow(
                item.TreeIndex,
                callbackOrdinal,
                item.Enabled,
                item.ItemLabel,
                item.RendererNodeText));
        }

        return rows;
    }

    public string BuildFingerprint()
    {
        var builder = new StringBuilder(512);
        Append(builder, CharacterContentId.ToString(CultureInfo.InvariantCulture));
        Append(builder, AddonIdentity.ToString("X", CultureInfo.InvariantCulture));
        Append(builder, AgentIdentity.ToString("X", CultureInfo.InvariantCulture));
        Append(builder, DutyListIdentity.ToString("X", CultureInfo.InvariantCulture));
        Append(builder, ContentListStorageIdentity.ToString("X", CultureInfo.InvariantCulture));
        Append(builder, DutyListStorageIdentity.ToString("X", CultureInfo.InvariantCulture));
        Append(builder, SelectedTab.ToString(CultureInfo.InvariantCulture));
        Append(builder, SelectedRadioButton.ToString(CultureInfo.InvariantCulture));
        Append(builder, DeclaredEntryCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, TreeItemCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, ContentEntries.Count.ToString(CultureInfo.InvariantCulture));
        Append(builder, TreeItems.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var entry in ContentEntries)
        {
            Append(builder, entry.ContentListIndex.ToString(CultureInfo.InvariantCulture));
            Append(builder, ((int)entry.ContentType).ToString(CultureInfo.InvariantCulture));
            Append(builder, entry.RowId.ToString(CultureInfo.InvariantCulture));
            Append(builder, entry.LocalizedName);
        }

        foreach (var item in TreeItems)
        {
            Append(builder, item.TreeIndex.ToString(CultureInfo.InvariantCulture));
            Append(builder, item.IsLeaf ? "1" : "0");
            Append(builder, item.Enabled ? "1" : "0");
            Append(builder, item.ItemLabel);
            // RendererNodeText is intentionally excluded. It is diagnostic
            // evidence only and never participates in selection authority.
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }
}

internal readonly record struct DadDutyFinderSelectionToken(
    DadDutyFinderLiveTarget Target,
    ulong CharacterContentId,
    string ListFingerprint,
    int TreeIndex,
    int CallbackOrdinal);

internal sealed record DadDutyFinderResolvedEntry(
    DadDutyFinderContentEntry Content,
    DadDutyFinderUiRow UiRow,
    DadDutyFinderSelectionToken SelectionToken)
{
    public int ObservedListPosition => Content.ContentListIndex + 1;
}

internal enum DadDutyFinderMappingStatus
{
    Unstable,
    AwaitingStableSnapshot,
    Absent,
    Disabled,
    Ambiguous,
    Mismatch,
    Ready,
}

internal sealed record DadDutyFinderMappingResult(
    DadDutyFinderMappingStatus Status,
    string Reason,
    DadDutyFinderResolvedEntry? Entry = null)
{
    public bool IsReady => Status == DadDutyFinderMappingStatus.Ready && Entry != null;
}

internal static class DadDutyFinderLiveEntryMapping
{
    public static DadDutyFinderMappingResult Resolve(
        DadDutyFinderListSnapshot snapshot,
        DadDutyFinderLiveTarget target,
        string stableFingerprint)
    {
        if (target.ContentType == DadDutyFinderLiveContentType.None || target.RowId == 0)
            return Mismatch("Target content type and row ID must both be exact and non-zero.");

        if (snapshot.TreeItems.Count != snapshot.TreeItemCount)
        {
            return Mismatch(
                $"Captured tree item count {snapshot.TreeItems.Count} does not match live count {snapshot.TreeItemCount}.");
        }

        for (var index = 0; index < snapshot.TreeItems.Count; index++)
        {
            if (snapshot.TreeItems[index].TreeIndex != index)
                return Mismatch($"Tree item order changed at captured index {index}.");
        }

        for (var index = 0; index < snapshot.ContentEntries.Count; index++)
        {
            var entry = snapshot.ContentEntries[index];
            if (entry.ContentListIndex != index)
                return Mismatch($"Agent content order changed at captured index {index}.");
            if (entry.ContentType == DadDutyFinderLiveContentType.None || entry.RowId == 0)
                return Mismatch($"Agent content entry {index + 1} has no authoritative type or row ID.");
            if (entry.ContentType != target.ContentType)
            {
                return Mismatch(
                    $"Agent content entry {index + 1} has type {entry.ContentType} while the hydrated target list requires {target.ContentType}.");
            }
        }

        var duplicateIdentity = snapshot.ContentEntries
            .GroupBy(static entry => (entry.ContentType, entry.RowId))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateIdentity != null)
        {
            return Ambiguous(
                $"Agent content identity {duplicateIdentity.Key.ContentType}:{duplicateIdentity.Key.RowId} appears more than once.");
        }

        var duplicateName = snapshot.ContentEntries
            .Select(static entry => new
            {
                entry.ContentType,
                Name = NormalizeLocalizedName(entry.LocalizedName),
            })
            .Where(static entry => !string.IsNullOrEmpty(entry.Name))
            .GroupBy(static entry => (entry.ContentType, entry.Name))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateName != null)
        {
            return Ambiguous(
                $"Agent content name '{duplicateName.Key.Name}' is duplicated for type {duplicateName.Key.ContentType}.");
        }

        var uiRows = snapshot.BuildUiRows();
        var correlation = CorrelateEntries(snapshot.ContentEntries, uiRows);
        if (correlation.Status != DadDutyFinderMappingStatus.Ready)
            return new DadDutyFinderMappingResult(correlation.Status, correlation.Reason);

        var targetMatches = snapshot.ContentEntries
            .Select((entry, index) => (entry, index))
            .Where(candidate =>
                candidate.entry.ContentType == target.ContentType &&
                candidate.entry.RowId == target.RowId)
            .ToList();
        if (targetMatches.Count == 0)
            return new DadDutyFinderMappingResult(
                DadDutyFinderMappingStatus.Absent,
                $"Exact target {target.ContentType}:{target.RowId} is absent from the live content list.");
        if (targetMatches.Count != 1)
            return Ambiguous($"Exact target {target.ContentType}:{target.RowId} is not unique.");

        var targetMatch = targetMatches[0];
        var targetRow = correlation.RowsByContentIndex[targetMatch.index];
        var token = new DadDutyFinderSelectionToken(
            target,
            snapshot.CharacterContentId,
            stableFingerprint,
            targetRow.TreeIndex,
            targetRow.CallbackOrdinal);
        var resolvedEntry = new DadDutyFinderResolvedEntry(targetMatch.entry, targetRow, token);
        if (!targetRow.Enabled)
        {
            return new DadDutyFinderMappingResult(
                DadDutyFinderMappingStatus.Disabled,
                $"Exact target {target.ContentType}:{target.RowId} is present but disabled.",
                resolvedEntry);
        }

        return new DadDutyFinderMappingResult(
            DadDutyFinderMappingStatus.Ready,
            string.Empty,
            resolvedEntry);
    }

    public static string NormalizeLocalizedName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var whitespacePending = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.Surrogate or
                UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (whitespacePending)
            {
                builder.Append(' ');
                whitespacePending = false;
            }

            builder.Append(Rune.ToUpperInvariant(rune));
        }

        return builder.ToString().Trim();
    }

    private static DadDutyFinderEntryCorrelation CorrelateEntries(
        IReadOnlyList<DadDutyFinderContentEntry> contentEntries,
        IReadOnlyList<DadDutyFinderUiRow> uiRows)
    {
        var contentNames = contentEntries
            .Select(static entry => NormalizeLocalizedName(entry.LocalizedName))
            .ToArray();
        var rowNames = uiRows
            .Select(static row => NormalizeLocalizedName(row.ItemLabel))
            .ToArray();

        for (var index = 0; index < contentNames.Length; index++)
        {
            if (string.IsNullOrEmpty(contentNames[index]))
            {
                return DadDutyFinderEntryCorrelation.Mismatch(
                    $"Agent content entry {index + 1} has no localized name.");
            }
        }

        for (var index = 0; index < rowNames.Length; index++)
        {
            if (string.IsNullOrEmpty(rowNames[index]))
            {
                return DadDutyFinderEntryCorrelation.Mismatch(
                    $"DutyList leaf at callback ordinal {uiRows[index].CallbackOrdinal} has no item-data label.");
            }
        }

        // ContentList can omit locked leaves while DutyList retains them. Build
        // a unique, order-preserving alignment in which every agent entry and
        // every enabled UI leaf must be consumed. Disabled leaves are optional
        // for correlation but always retain their callback ordinal.
        var ways = new byte[contentEntries.Count + 1, uiRows.Count + 1];
        ways[contentEntries.Count, uiRows.Count] = 1;
        for (var rowIndex = uiRows.Count - 1; rowIndex >= 0; rowIndex--)
        {
            ways[contentEntries.Count, rowIndex] = uiRows[rowIndex].Enabled
                ? (byte)0
                : ways[contentEntries.Count, rowIndex + 1];
        }

        for (var contentIndex = contentEntries.Count - 1; contentIndex >= 0; contentIndex--)
        {
            for (var rowIndex = uiRows.Count - 1; rowIndex >= 0; rowIndex--)
            {
                var matchWays = string.Equals(contentNames[contentIndex], rowNames[rowIndex], StringComparison.Ordinal)
                    ? ways[contentIndex + 1, rowIndex + 1]
                    : (byte)0;
                var skipWays = uiRows[rowIndex].Enabled
                    ? (byte)0
                    : ways[contentIndex, rowIndex + 1];
                ways[contentIndex, rowIndex] = (byte)Math.Min(2, matchWays + skipWays);
            }
        }

        if (ways[0, 0] == 0)
        {
            return DadDutyFinderEntryCorrelation.Mismatch(
                $"Agent content entries do not match the enabled DutyList leaves in localized-name order (agent={contentEntries.Count}, enabledLeaves={uiRows.Count(static row => row.Enabled)}, allLeaves={uiRows.Count}).");
        }

        if (ways[0, 0] > 1)
        {
            return DadDutyFinderEntryCorrelation.Ambiguous(
                "Agent content entries have more than one valid DutyList leaf alignment.");
        }

        var rowsByContentIndex = new DadDutyFinderUiRow[contentEntries.Count];
        var currentContent = 0;
        var currentRow = 0;
        while (currentContent < contentEntries.Count)
        {
            if (currentRow >= uiRows.Count)
            {
                return DadDutyFinderEntryCorrelation.Mismatch(
                    "Unique DutyList alignment ended before every agent content entry was correlated.");
            }

            var matchWays = string.Equals(contentNames[currentContent], rowNames[currentRow], StringComparison.Ordinal)
                ? ways[currentContent + 1, currentRow + 1]
                : (byte)0;
            var skipWays = uiRows[currentRow].Enabled
                ? (byte)0
                : ways[currentContent, currentRow + 1];
            if (matchWays == 1 && skipWays == 0)
            {
                rowsByContentIndex[currentContent] = uiRows[currentRow];
                currentContent++;
                currentRow++;
                continue;
            }

            if (matchWays == 0 && skipWays == 1)
            {
                currentRow++;
                continue;
            }

            return DadDutyFinderEntryCorrelation.Ambiguous(
                "DutyList alignment became ambiguous while correlating agent entries.");
        }

        return DadDutyFinderEntryCorrelation.Ready(rowsByContentIndex);
    }

    private static DadDutyFinderMappingResult Ambiguous(string reason)
        => new(DadDutyFinderMappingStatus.Ambiguous, reason);

    private static DadDutyFinderMappingResult Mismatch(string reason)
        => new(DadDutyFinderMappingStatus.Mismatch, reason);

    private sealed record DadDutyFinderEntryCorrelation(
        DadDutyFinderMappingStatus Status,
        string Reason,
        IReadOnlyList<DadDutyFinderUiRow> RowsByContentIndex)
    {
        public static DadDutyFinderEntryCorrelation Ready(IReadOnlyList<DadDutyFinderUiRow> rows)
            => new(DadDutyFinderMappingStatus.Ready, string.Empty, rows);

        public static DadDutyFinderEntryCorrelation Ambiguous(string reason)
            => new(DadDutyFinderMappingStatus.Ambiguous, reason, []);

        public static DadDutyFinderEntryCorrelation Mismatch(string reason)
            => new(DadDutyFinderMappingStatus.Mismatch, reason, []);
    }
}

internal sealed class DadDutyFinderStableMappingGate
{
    private DadDutyFinderLiveTarget? previousTarget;
    private string previousFingerprint = string.Empty;
    private int stableObservationCount;
    private long generation;

    public DadDutyFinderMappingResult Observe(
        DadDutyFinderListSnapshot snapshot,
        DadDutyFinderLiveTarget target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.ListChanged)
            return Invalidate("AgentContentsFinder reports ListChanged; waiting for a completed list generation.");

        if (snapshot.CharacterContentId == 0 ||
            snapshot.AddonIdentity == 0 ||
            snapshot.AgentIdentity == 0 ||
            snapshot.DutyListIdentity == 0)
        {
            return Invalidate("Current character, Duty Finder addon, agent, or DutyList identity is unavailable.");
        }

        var fingerprint = snapshot.BuildFingerprint();
        if (previousTarget != target || !string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
        {
            generation++;
            previousTarget = target;
            previousFingerprint = fingerprint;
            stableObservationCount = 1;
            return new DadDutyFinderMappingResult(
                DadDutyFinderMappingStatus.AwaitingStableSnapshot,
                "Waiting for a second consecutive identical Duty Finder list snapshot.");
        }

        stableObservationCount = Math.Min(2, stableObservationCount + 1);
        if (stableObservationCount < 2)
        {
            return new DadDutyFinderMappingResult(
                DadDutyFinderMappingStatus.AwaitingStableSnapshot,
                "Waiting for a second consecutive identical Duty Finder list snapshot.");
        }

        return DadDutyFinderLiveEntryMapping.Resolve(
            snapshot,
            target,
            $"{fingerprint}:G{generation.ToString(CultureInfo.InvariantCulture)}");
    }

    public void Reset()
    {
        previousTarget = null;
        previousFingerprint = string.Empty;
        stableObservationCount = 0;
        generation++;
    }

    private DadDutyFinderMappingResult Invalidate(string reason)
    {
        Reset();
        return new DadDutyFinderMappingResult(DadDutyFinderMappingStatus.Unstable, reason);
    }
}

internal static class DadDutyFinderMappedMutationRules
{
    public static bool ShouldSelect(
        DadDutyFinderMappingResult mapping,
        DadDutyFinderSelectionToken? lastSelectionToken)
        => mapping.IsReady && mapping.Entry!.SelectionToken != lastSelectionToken;

    public static bool CanJoin(
        DadDutyFinderMappingResult mapping,
        DadDutyFinderSelectionToken? lastSelectionToken,
        DadDutyFinderLiveContentType selectedAgentType,
        uint selectedAgentId,
        DadDutyFinderLiveTarget target)
        => mapping.IsReady &&
           mapping.Entry!.SelectionToken == lastSelectionToken &&
           selectedAgentType == target.ContentType &&
           selectedAgentId == target.RowId;

    public static bool ShouldAwaitRegularPostSelectionMapping(
        DadDutyFinderMappingResult mapping,
        DadDutyFinderSelectionToken? selectionToken)
        => selectionToken != null &&
           mapping.Status is DadDutyFinderMappingStatus.Unstable or
               DadDutyFinderMappingStatus.AwaitingStableSnapshot;

    public static bool CanJoinRegularAfterSelection(
        DadDutyFinderMappingResult mapping,
        DadDutyFinderSelectionToken? selectionToken,
        DadDutyFinderLiveContentType selectedAgentType,
        uint selectedAgentId,
        uint interfaceSelectedId,
        DadDutyFinderLiveTarget target)
    {
        if (!mapping.IsReady || selectionToken == null)
            return false;

        var fresh = mapping.Entry!.SelectionToken;
        var selected = selectionToken.Value;
        return fresh.Target == target &&
               selected.Target == target &&
               fresh.CharacterContentId == selected.CharacterContentId &&
               fresh.TreeIndex == selected.TreeIndex &&
               fresh.CallbackOrdinal == selected.CallbackOrdinal &&
               selectedAgentType == target.ContentType &&
               selectedAgentId == target.RowId;
    }
}
