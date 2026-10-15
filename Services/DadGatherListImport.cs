using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

internal static class DadGatherListImport
{
    private const int MaximumInputBytes = 1024 * 1024;

    // Native GBR: Base64(GZip(version byte + UTF-8 Config JSON)).
    // Teamcraft's text export uses quantity-first lines, e.g. "1000x Iron Ore".
    public static DadGatherImportResult Parse(string text, Func<string, uint> identify)
    {
        ArgumentNullException.ThrowIfNull(identify);
        var task = new DadGatherPartyTask();
        var issues = new List<DadGatherImportIssue>();
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaximumInputBytes)
            return new(task, [new(0, "Paste a gathering export no larger than 1 MiB.")]);
        var trimmed = text.Trim();
        if (trimmed.StartsWith("H4sI", StringComparison.Ordinal))
        {
            try { ReadGbr(trimmed, task, issues); }
            catch (Exception exception) when (exception is FormatException or InvalidDataException or IOException or
                                                JsonException or OverflowException or DecoderFallbackException or InvalidOperationException or KeyNotFoundException)
            {
                issues.Add(new(0, "The GBR export is invalid: " + exception.Message));
            }
        }
        else
            ReadText(trimmed, identify, task, issues);
        if (task.Items.Count == 0 && issues.Count == 0)
            issues.Add(new(0, "The export contains no gathering targets."));
        return new(task, issues);
    }

    private static void ReadGbr(string text, DadGatherPartyTask task, List<DadGatherImportIssue> issues)
    {
        var compressed = Convert.FromBase64String(text);
        using var source = new MemoryStream(compressed, false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        var version = gzip.ReadByte();
        if (version is not (4 or 5 or 6))
            throw new InvalidDataException("Unsupported GBR list version.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (output.Length + read > MaximumInputBytes)
                throw new InvalidDataException("The expanded GBR list exceeds 1 MiB.");
            output.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var ids = root.GetProperty("ItemIds");
        var quantities = root.GetProperty("Quantities");
        var locations = root.GetProperty("PrefferedLocations"); // Native spelling, including v4/v5 exports.
        if (ids.ValueKind != JsonValueKind.Array || quantities.ValueKind != JsonValueKind.Object || locations.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing GBR items, quantities or preferred locations.");
        if (ids.GetArrayLength() > DadGatherPartyPlanningRules.MaximumItems)
            throw new InvalidDataException("The GBR list exceeds the item limit.");
        if (root.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
            task.Name = name.GetString() ?? task.Name;
        root.TryGetProperty("EnabledItems", out var enabled);
        var seen = new HashSet<uint>();
        var line = 0;
        foreach (var idValue in ids.EnumerateArray())
        {
            line++;
            var id = idValue.GetUInt32();
            var key = id.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(id))
                issues.Add(new(line, $"GBR item {id} appears more than once."));
            var quantity = quantities.TryGetProperty(key, out var count) ? count.GetInt32() : 0;
            var target = new DadGatherItemTarget
            {
                ItemId = id,
                TargetQuantity = quantity,
                PreferredLocationId = locations.TryGetProperty(key, out var location) ? location.GetUInt32() : 0,
                Enabled = enabled.ValueKind != JsonValueKind.Object || !enabled.TryGetProperty(key, out var active) || active.GetBoolean(),
            };
            task.Items.Add(target);
            Validate(target, line, issues);
        }
    }

    private static void ReadText(string text, Func<string, uint> identify, DadGatherPartyTask task, List<DadGatherImportIssue> issues)
    {
        task.Name = "Imported gathering list";
        using var reader = new StringReader(text);
        string? line;
        var number = 0;
        while ((line = reader.ReadLine()) != null)
        {
            number++;
            var value = line.Trim();
            if (value.Length == 0 || value.EndsWith(':'))
                continue;
            if (task.Items.Count >= DadGatherPartyPlanningRules.MaximumItems)
            {
                issues.Add(new(number, "The gathering export exceeds the item limit."));
                break;
            }
            var columns = value.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            uint id;
            int quantity;
            string itemName;
            if (columns.Length == 2 && uint.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
                int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out quantity))
                itemName = string.Empty;
            else
            {
                var separator = value.IndexOfAny(['x', '×']);
                if (separator < 1 || !int.TryParse(value[..separator].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out quantity) ||
                    string.IsNullOrWhiteSpace(itemName = value[(separator + 1)..].Trim()))
                {
                    issues.Add(new(number, "Expected an item-ID/quantity row or a Teamcraft quantity-first item line."));
                    continue;
                }
                id = identify(itemName);
            }
            var target = new DadGatherItemTarget { ItemId = id, Name = itemName, TargetQuantity = quantity };
            Validate(target, number, issues);
            var existing = id == 0 ? null : task.Items.FirstOrDefault(item => item.ItemId == id);
            if (existing == null)
                task.Items.Add(target);
            else
            {
                var total = (long)existing.TargetQuantity + quantity;
                if (total is < 1 or > DadGatherPartyPlanningRules.MaximumQuantity)
                    issues.Add(new(number, $"Combined quantity for item {id} must be from 1 to 999999."));
                else
                    existing.TargetQuantity = (int)total;
            }
        }
    }

    private static void Validate(DadGatherItemTarget item, int line, List<DadGatherImportIssue> issues)
    {
        if (item.ItemId == 0)
            issues.Add(new(line, "The item is unknown or cannot be resolved to a gatherable item ID."));
        if (item.TargetQuantity is < 1 or > DadGatherPartyPlanningRules.MaximumQuantity)
            issues.Add(new(line, "Gathering quantities must be from 1 to 999999."));
    }
}
