using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadDependencyService : IDisposable
{
    private static readonly TimeSpan CheckingRetryInterval = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private DadDependencySnapshot snapshot = DadDependencySnapshot.CreateChecking();
    private DateTime nextInspectionUtc = DateTime.MinValue;
    private long revision;
    private bool dirty = true;
    private bool disposed;

    public DadDependencyService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }

    public DadDependencySnapshot Snapshot
    {
        get
        {
            lock (gate)
                return snapshot.Clone();
        }
    }

    public bool IsReady => Snapshot.IsReady;

    public void MarkDirty(string summary = "Required plugin state changed; checking again.")
    {
        lock (gate)
        {
            if (!dirty)
                revision++;
            dirty = true;
            nextInspectionUtc = DateTime.MinValue;
            snapshot = DadDependencySnapshot.CreateChecking(revision, snapshot, summary);
        }
    }

    public DadDependencySnapshot ForceInspect(bool enabled)
    {
        MarkDirty("Rechecking required plugins.");
        Update(enabled, force: true);
        return Snapshot;
    }

    /// <summary>
    /// Called from Dalamud's framework thread. Plugin-list enumeration never occurs in an event callback.
    /// </summary>
    public void Update(bool enabled, bool force = false)
    {
        if (!enabled || disposed)
            return;

        var nowUtc = DateTime.UtcNow;
        lock (gate)
        {
            if (!force && !dirty && snapshot.AggregateState != DadDependencyState.Checking)
                return;
            if (!force && nowUtc < nextInspectionUtc)
                return;
            nextInspectionUtc = nowUtc + CheckingRetryInterval;
        }

        try
        {
            var installed = pluginInterface.InstalledPlugins;
            if (installed == null)
            {
                RetainChecking("Dalamud's installed-plugin list is unavailable; checking again.");
                return;
            }

            var metadata = new List<DadInstalledPluginMetadata>();
            foreach (var plugin in installed)
            {
                if (plugin == null || string.IsNullOrWhiteSpace(plugin.InternalName))
                {
                    RetainChecking("Dalamud returned incomplete plugin metadata; checking again.");
                    return;
                }

                metadata.Add(new DadInstalledPluginMetadata(
                    plugin.InternalName,
                    plugin.Name ?? plugin.InternalName,
                    plugin.Version?.ToString() ?? string.Empty,
                    plugin.IsLoaded,
                    plugin.IsOutdated));
            }

            lock (gate)
            {
                revision++;
                snapshot = DadDependencyRules.Evaluate(metadata, revision, nowUtc);
                dirty = false;
                nextInspectionUtc = snapshot.AggregateState == DadDependencyState.Checking
                    ? nowUtc + CheckingRetryInterval
                    : DateTime.MaxValue;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][Dependencies] Failed to inspect Dalamud's installed-plugin list.");
            RetainChecking("Required plugin metadata could not be inspected; checking again.");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs _)
        => MarkDirty();

    private void RetainChecking(string summary)
    {
        lock (gate)
        {
            dirty = true;
            snapshot = DadDependencySnapshot.CreateChecking(revision, snapshot, summary);
            nextInspectionUtc = DateTime.UtcNow + CheckingRetryInterval;
        }
    }
}
