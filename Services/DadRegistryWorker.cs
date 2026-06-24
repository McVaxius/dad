using System.Text;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

internal sealed class DadRegistryWorker : IDisposable
{
    private static readonly TimeSpan RegistryReadInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RegistryCollisionWarningInterval = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private readonly string registryDirectory;
    private readonly string registryFilePath;
    private readonly string clientInstanceId;
    private readonly IPluginLog log;
    private readonly Dictionary<string, DateTime> lastRegistryReadWarningUtcByPath = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextRegistryReadUtc = DateTime.MinValue;
    private DateTime lastRegistryWriteWarningUtc = DateTime.MinValue;
    private DadTransportRegistryEntry? pendingHeartbeat;
    private DadRegistryReadSnapshot? latestSnapshot;
    private long latestSnapshotGeneration;
    private long consumedSnapshotGeneration;
    private bool readRunning;
    private bool writeRunning;
    private bool disposed;

    public DadRegistryWorker(
        string registryDirectory,
        string registryFilePath,
        string clientInstanceId,
        IPluginLog log)
    {
        this.registryDirectory = registryDirectory;
        this.registryFilePath = registryFilePath;
        this.clientInstanceId = clientInstanceId;
        this.log = log;
    }

    public void EnsureReadScheduled()
    {
        lock (gate)
        {
            if (disposed || readRunning || DateTime.UtcNow < nextRegistryReadUtc)
                return;

            readRunning = true;
            nextRegistryReadUtc = DateTime.UtcNow + RegistryReadInterval;
        }

        _ = Task.Run(ReadRegistrySnapshot);
    }

    public bool TryConsumeLatestSnapshot(out DadRegistryReadSnapshot snapshot)
    {
        lock (gate)
        {
            if (latestSnapshot == null || consumedSnapshotGeneration == latestSnapshot.Generation)
            {
                snapshot = DadRegistryReadSnapshot.Empty;
                return false;
            }

            consumedSnapshotGeneration = latestSnapshot.Generation;
            snapshot = latestSnapshot;
            return true;
        }
    }

    public void QueueHeartbeat(DadTransportRegistryEntry entry)
    {
        lock (gate)
        {
            if (disposed)
                return;

            pendingHeartbeat = entry.Clone();
            if (writeRunning)
                return;

            writeRunning = true;
        }

        _ = Task.Run(WriteHeartbeatLoop);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pendingHeartbeat = null;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (File.Exists(registryFilePath))
                    File.Delete(registryFilePath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        });
    }

    private void ReadRegistrySnapshot()
    {
        try
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<string, DadTransportRegistryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(registryDirectory, "*.json"))
            {
                seenPaths.Add(path);
                try
                {
                    var entry = ReadRegistryEntry(path);
                    if (entry != null)
                        entries[path] = entry;
                }
                catch (FileNotFoundException)
                {
                    // A peer disappeared between enumeration and read; the framework cache will expire it.
                }
                catch (IOException ex)
                {
                    LogRegistryReadCollision(path, ex);
                }
                catch (Exception ex)
                {
                    LogRegistryReadFailure(path, ex);
                }
            }

            lock (gate)
            {
                latestSnapshot = new DadRegistryReadSnapshot(
                    ++latestSnapshotGeneration,
                    DateTime.UtcNow,
                    entries,
                    seenPaths);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Transport registry refresh failed.");
        }
        finally
        {
            lock (gate)
            {
                readRunning = false;
            }
        }
    }

    private void WriteHeartbeatLoop()
    {
        while (true)
        {
            DadTransportRegistryEntry? entry;
            lock (gate)
            {
                if (disposed)
                {
                    writeRunning = false;
                    pendingHeartbeat = null;
                    return;
                }

                entry = pendingHeartbeat;
                pendingHeartbeat = null;
                if (entry == null)
                {
                    writeRunning = false;
                    return;
                }
            }

            try
            {
                WriteRegistryEntryAtomically(entry);
            }
            catch (IOException ex)
            {
                if (ShouldLogRegistryWarning(ref lastRegistryWriteWarningUtc))
                    log.Warning(ex, "[dad] Transport registry heartbeat collision for {RegistryFilePath}; keeping previous discovery entry.", registryFilePath);
                else
                    log.Debug(ex, "[dad] Transport registry heartbeat collision for {RegistryFilePath}.", registryFilePath);
            }
            catch (Exception ex)
            {
                if (ShouldLogRegistryWarning(ref lastRegistryWriteWarningUtc))
                    log.Warning(ex, "[dad] Failed to write transport registry entry.");
                else
                    log.Debug(ex, "[dad] Failed to write transport registry entry.");
            }
        }
    }

    private void WriteRegistryEntryAtomically(DadTransportRegistryEntry entry)
    {
        Directory.CreateDirectory(registryDirectory);
        var tempPath = Path.Combine(
            registryDirectory,
            $"{clientInstanceId}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var payload = DadIpcJson.Serialize(entry);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(registryFilePath))
                File.Replace(tempPath, registryFilePath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, registryFilePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp cleanup only.
            }
        }
    }

    private static DadTransportRegistryEntry? ReadRegistryEntry(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();
        return DadIpcJson.Deserialize<DadTransportRegistryEntry>(json);
    }

    private void LogRegistryReadCollision(string path, IOException ex)
    {
        if (ShouldLogRegistryWarning(path))
            log.Warning(ex, "[dad] Transport registry read collision for {RegistryFilePath}; keeping cached peer state until heartbeat expires.", path);
        else
            log.Debug(ex, "[dad] Transport registry read collision for {RegistryFilePath}.", path);
    }

    private void LogRegistryReadFailure(string path, Exception ex)
    {
        if (ShouldLogRegistryWarning(path))
            log.Warning(ex, "[dad] Failed to read transport registry entry {RegistryFilePath}; keeping cached peer state until heartbeat expires.", path);
        else
            log.Debug(ex, "[dad] Failed to read transport registry entry {RegistryFilePath}.", path);
    }

    private bool ShouldLogRegistryWarning(string path)
    {
        if (!lastRegistryReadWarningUtcByPath.TryGetValue(path, out var lastLoggedUtc))
        {
            lastRegistryReadWarningUtcByPath[path] = DateTime.UtcNow;
            return true;
        }

        if (DateTime.UtcNow - lastLoggedUtc < RegistryCollisionWarningInterval)
            return false;

        lastRegistryReadWarningUtcByPath[path] = DateTime.UtcNow;
        return true;
    }

    private static bool ShouldLogRegistryWarning(ref DateTime lastLoggedUtc)
    {
        if (lastLoggedUtc == DateTime.MinValue || DateTime.UtcNow - lastLoggedUtc >= RegistryCollisionWarningInterval)
        {
            lastLoggedUtc = DateTime.UtcNow;
            return true;
        }

        return false;
    }
}

internal sealed record DadRegistryReadSnapshot(
    long Generation,
    DateTime ReadAtUtc,
    IReadOnlyDictionary<string, DadTransportRegistryEntry> Entries,
    IReadOnlySet<string> SeenPaths)
{
    public static DadRegistryReadSnapshot Empty { get; } = new(
        0,
        DateTime.MinValue,
        new Dictionary<string, DadTransportRegistryEntry>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

internal sealed class DadTransportRegistryEntry
{
    public string ClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public string Endpoint { get; set; } = string.Empty;
    public DateTime HeartbeatUtc { get; set; } = DateTime.UtcNow;
    public DadParticipantSnapshot Participant { get; set; } = new();

    public DadTransportRegistryEntry Clone()
        => new()
        {
            ClientInstanceId = ClientInstanceId,
            WorkerSessionId = WorkerSessionId,
            Endpoint = Endpoint,
            HeartbeatUtc = HeartbeatUtc,
            Participant = Participant.Clone(),
        };
}
