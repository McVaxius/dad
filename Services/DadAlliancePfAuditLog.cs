using System.Text;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

public sealed class DadAlliancePfAuditRecord
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Event { get; set; } = string.Empty;
    public string RecruitmentId { get; set; } = string.Empty;
    public ulong PfOwnerHandle { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string HostWorld { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string TargetWorld { get; set; } = string.Empty;
    public string TargetCharacterKey { get; set; } = string.Empty;
    public ulong TargetContentId { get; set; }
    public DadAllianceAssignment ExpectedAlliance { get; set; }
    public DadAllianceAssignment ObservedAlliance { get; set; }
    public int Passcode { get; set; }
    public int Attempt { get; set; }
    public string CreateStage { get; set; } = string.Empty;
    public DateTime? NextRetryUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string Readiness { get; set; } = string.Empty;
    public uint Category { get; set; }
    public ushort DutyId { get; set; }
    public bool ActiveRecruitment { get; set; }
    public bool EditorVisible { get; set; }
    public bool SubmitDispatched { get; set; }
    public string ConfigurationTarget { get; set; } = string.Empty;
    public string ObservedSettings { get; set; } = string.Empty;
    public int ElapsedMilliseconds { get; set; }
    public long StopGeneration { get; set; }
    public string Transport { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public Dictionary<string, string> Evidence { get; set; } = [];
}

/// <summary>
/// Append-only local forensic log. Records intentionally contain exact local PF
/// identities and passcodes, but the API offers no token/key/transport-secret fields.
/// </summary>
public sealed class DadAlliancePfAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string logDirectory;
    private readonly string diagnosticsDirectory;
    private readonly Action<Exception>? failure;

    public DadAlliancePfAuditLog(string pluginConfigurationDirectory, Action<Exception>? failure = null)
    {
        if (string.IsNullOrWhiteSpace(pluginConfigurationDirectory))
            throw new ArgumentException("Plugin configuration directory is required.", nameof(pluginConfigurationDirectory));
        logDirectory = Path.Combine(
            Path.GetFullPath(pluginConfigurationDirectory),
            "alliance-pf",
            "logs");
        diagnosticsDirectory = Path.Combine(
            Path.GetFullPath(pluginConfigurationDirectory),
            "alliance-pf",
            "diagnostics");
        this.failure = failure;
    }

    public string GetPath(DateTime utcNow)
    {
        utcNow = EnsureUtc(utcNow);
        return Path.Combine(
            logDirectory,
            $"alliance-pf-{utcNow:yyyyMMdd}.jsonl");
    }

    public bool TryWrite(DadAlliancePfAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.TimestampUtc = EnsureUtc(record.TimestampUtc);
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
            lock (gate)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(GetPath(record.TimestampUtc), line, new UTF8Encoding(false));
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure?.Invoke(exception);
            return false;
        }
    }

    public bool TryWriteLookingForGroupDiagnostics(
        string content,
        DateTime utcNow,
        out string path,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(content);
        utcNow = EnsureUtc(utcNow);
        path = string.Empty;
        error = string.Empty;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                var stem =
                    $"looking-for-group-tree-{utcNow:yyyyMMddTHHmmss.fffffffZ}";
                for (var suffix = 0; suffix < 1_000; suffix++)
                {
                    var candidate = Path.Combine(
                        diagnosticsDirectory,
                        suffix == 0
                            ? $"{stem}.txt"
                            : $"{stem}-{suffix:000}.txt");
                    FileStream stream;
                    try
                    {
                        stream = new FileStream(
                            candidate,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None);
                    }
                    catch (IOException) when (File.Exists(candidate))
                    {
                        continue;
                    }

                    try
                    {
                        using (stream)
                        {
                            var bytes = new UTF8Encoding(false).GetBytes(content);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush(flushToDisk: true);
                        }

                        path = candidate;
                        return true;
                    }
                    catch
                    {
                        try
                        {
                            File.Delete(candidate);
                        }
                        catch
                        {
                        }

                        throw;
                    }
                }
            }

            error = "No unique diagnostics filename was available.";
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            failure?.Invoke(exception);
            return false;
        }
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
}
