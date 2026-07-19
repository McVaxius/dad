using System.Globalization;
using System.Text;
using dad.Models;

namespace dad.Services;

public static class DadAutoPartyFleetTsv
{
    public const string Header = "row_id\topaque_character_id\trole\tjob_id\tis_remote\tenabled\tcrew_set_id\tcrew_set_name\tcrew_order";

    public static string Export(DadAutoPartyFleetConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalized = configuration.Clone().Normalize();
        var assignments = normalized.CrewSets
            .SelectMany(crew => crew.FleetRowIds.Select((rowId, index) => new
            {
                RowId = rowId,
                Crew = crew,
                Order = index + 1,
            }))
            .ToDictionary(static item => item.RowId, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string> { Header };
        foreach (var row in normalized.Rows.OrderBy(static row => row.RowId, StringComparer.OrdinalIgnoreCase))
        {
            assignments.TryGetValue(row.RowId, out var assignment);
            lines.Add(string.Join('\t',
                Protect(row.RowId),
                Protect(row.OpaqueCharacterId),
                row.Role.ToString(),
                row.JobId.ToString(CultureInfo.InvariantCulture),
                row.IsRemote ? "true" : "false",
                row.Enabled ? "true" : "false",
                Protect(assignment?.Crew.CrewSetId ?? string.Empty),
                Protect(assignment?.Crew.DisplayName ?? string.Empty),
                assignment?.Order.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        }

        var result = string.Join("\r\n", lines) + "\r\n";
        if (Encoding.UTF8.GetByteCount(result) > DadAutoPartyFleetLimits.MaxTsvBytes)
            throw new InvalidOperationException("The Fleet TSV exceeds the defensive size limit.");
        return result;
    }

    public static DadAutoPartyFleetImportResult Parse(string? tsv)
    {
        var text = tsv ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > DadAutoPartyFleetLimits.MaxTsvBytes)
            return Failure("dad-fleet-tsv-too-large", "The Fleet TSV exceeds 256 KiB.");
        if (text.IndexOf('\0') >= 0 || text.Any(static character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            return Failure("dad-fleet-tsv-control-character", "The Fleet TSV contains an unsupported control character.");

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0], Header, StringComparison.Ordinal))
            return Failure("dad-fleet-tsv-header-invalid", "The Fleet TSV header does not match the supported schema.");

        var rows = new List<DadAutoPartyFleetRow>();
        var crewBuilders = new Dictionary<string, CrewBuilder>(StringComparer.OrdinalIgnoreCase);
        var rowIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (lineIndex == lines.Length - 1 && line.Length == 0)
                continue;
            if (string.IsNullOrWhiteSpace(line))
                return Failure("dad-fleet-tsv-blank-line", $"Line {lineIndex + 1} is blank.");
            if (rows.Count >= DadAutoPartyFleetLimits.MaxFleetRows)
                return Failure("dad-fleet-tsv-row-limit", $"The Fleet TSV exceeds {DadAutoPartyFleetLimits.MaxFleetRows} rows.");

            var fields = line.Split('\t');
            if (fields.Length != 9)
                return Failure("dad-fleet-tsv-column-count", $"Line {lineIndex + 1} must contain exactly 9 columns.");
            for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
            {
                if (!TryUnprotect(fields[fieldIndex], out fields[fieldIndex]))
                    return Failure("dad-fleet-tsv-formula-prefix", $"Line {lineIndex + 1}, column {fieldIndex + 1} has an unsafe spreadsheet formula prefix.");
                if (fields[fieldIndex].Length > DadAutoPartyFleetLimits.MaxTextLength)
                    return Failure("dad-fleet-tsv-field-too-long", $"Line {lineIndex + 1}, column {fieldIndex + 1} is too long.");
            }

            if (!Enum.TryParse<DadPartyRole>(fields[2], ignoreCase: true, out var role) || !Enum.IsDefined(role))
                return Failure("dad-fleet-tsv-role-invalid", $"Line {lineIndex + 1} has an invalid role.");
            if (!uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var jobId) || jobId == 0)
                return Failure("dad-fleet-tsv-job-invalid", $"Line {lineIndex + 1} requires a positive job ID.");
            if (!bool.TryParse(fields[4], out var isRemote) || !bool.TryParse(fields[5], out var enabled))
                return Failure("dad-fleet-tsv-boolean-invalid", $"Line {lineIndex + 1} has an invalid Boolean value.");

            var row = new DadAutoPartyFleetRow
            {
                RowId = fields[0],
                OpaqueCharacterId = fields[1],
                Role = role,
                JobId = jobId,
                IsRemote = isRemote,
                Enabled = enabled,
            }.Normalize();
            if (string.IsNullOrWhiteSpace(row.RowId) || !rowIds.Add(row.RowId))
                return Failure("dad-fleet-tsv-row-id-duplicate", $"Line {lineIndex + 1} has an empty or duplicate row ID.");
            if (row.IsRemote && string.IsNullOrWhiteSpace(row.OpaqueCharacterId))
                return Failure("dad-fleet-tsv-remote-id-missing", $"Line {lineIndex + 1} is remote but has no opaque character ID.");
            rows.Add(row);

            var crewId = DadAutoPartyFleetConfiguration.NormalizeIdentifier(fields[6]);
            var crewName = DadAutoPartyFleetConfiguration.NormalizeText(fields[7]);
            if (string.IsNullOrWhiteSpace(crewId))
            {
                if (!string.IsNullOrWhiteSpace(crewName) || !string.IsNullOrWhiteSpace(fields[8]))
                    return Failure("dad-fleet-tsv-crew-partial", $"Line {lineIndex + 1} has a partial Crew Set assignment.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(crewName) ||
                !int.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var crewOrder) ||
                crewOrder is < 1 or > DadAutoPartyFleetLimits.MaxCrewMembers)
                return Failure("dad-fleet-tsv-crew-invalid", $"Line {lineIndex + 1} has an invalid Crew Set assignment.");
            if (!crewBuilders.TryGetValue(crewId, out var builder))
            {
                if (crewBuilders.Count >= DadAutoPartyFleetLimits.MaxCrewSets)
                    return Failure("dad-fleet-tsv-crew-limit", $"The Fleet TSV exceeds {DadAutoPartyFleetLimits.MaxCrewSets} Crew Sets.");
                builder = new CrewBuilder(crewId, crewName);
                crewBuilders.Add(crewId, builder);
            }
            if (!string.Equals(builder.DisplayName, crewName, StringComparison.Ordinal))
                return Failure("dad-fleet-tsv-crew-name-conflict", $"Crew Set '{crewId}' has inconsistent names.");
            if (!builder.TryAdd(crewOrder, row.RowId))
                return Failure("dad-fleet-tsv-crew-order-duplicate", $"Crew Set '{crewId}' repeats slot {crewOrder}.");
        }

        var crews = crewBuilders.Values
            .OrderBy(static builder => builder.CrewSetId, StringComparer.OrdinalIgnoreCase)
            .Select(static builder => builder.Build())
            .ToList();
        return new(true, "dad-fleet-tsv-valid", $"Validated {rows.Count} Fleet row(s) and {crews.Count} Crew Set(s).", new(rows, crews));
    }

    private static string Protect(string value)
    {
        if (value.Any(static character => character is '\r' or '\n' or '\t' or '\0' || (char.IsControl(character) && character != '\t')))
            throw new InvalidOperationException("A Fleet TSV field contains an unsupported control character.");
        return IsFormulaPrefix(value) ? "'" + value : value;
    }

    private static bool TryUnprotect(string value, out string unprotected)
    {
        if (value.Length >= 2 && value[0] == '\'' && IsFormulaPrefix(value[1..]))
        {
            unprotected = value[1..];
            return true;
        }
        unprotected = value;
        return !IsFormulaPrefix(value);
    }

    private static bool IsFormulaPrefix(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@';

    private static DadAutoPartyFleetImportResult Failure(string safeCode, string summary)
        => new(false, safeCode, summary);

    private sealed class CrewBuilder(string crewSetId, string displayName)
    {
        private readonly SortedDictionary<int, string> rows = [];

        public string CrewSetId { get; } = crewSetId;
        public string DisplayName { get; } = displayName;

        public bool TryAdd(int order, string rowId)
            => rows.TryAdd(order, rowId);

        public DadAutoPartyCrewSet Build()
            => new()
            {
                CrewSetId = CrewSetId,
                DisplayName = DisplayName,
                FleetRowIds = rows.Values.ToList(),
            };
    }
}
