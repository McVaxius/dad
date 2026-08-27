using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace dad.Services;

internal sealed class DadKranglerPrivacyLeaseService : IDisposable
{
    internal const string AcquireChannel = "Krangler.DadPrivacyLease.AcquireFromJson";
    internal const string ReleaseChannel = "Krangler.DadPrivacyLease.ReleaseFromJson";
    internal const string StatusChannel = "Krangler.DadPrivacyLease.GetStatusJson";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly DadKranglerPrivacyLeaseController controller;
    private bool disposed;

    internal DadKranglerPrivacyLeaseService(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        ArgumentNullException.ThrowIfNull(log);
        ICallGateSubscriber<string, string> acquire = pluginInterface.GetIpcSubscriber<string, string>(AcquireChannel);
        ICallGateSubscriber<string, string> release = pluginInterface.GetIpcSubscriber<string, string>(ReleaseChannel);
        ICallGateSubscriber<string, string> status = pluginInterface.GetIpcSubscriber<string, string>(StatusChannel);
        controller = new DadKranglerPrivacyLeaseController(
            acquire.InvokeFunc,
            release.InvokeFunc,
            status.InvokeFunc,
            (exception, operation) =>
                log.Debug(exception, "[dad][KranglerPrivacy] IPC {Operation} is unavailable.", operation));
        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }

    internal DadKranglerPrivacyLeaseSnapshot Snapshot => controller.Snapshot;

    internal void Update(bool desired)
    {
        controller.SetDesired(desired);
        controller.Update();
    }

    internal void SetDesired(bool desired)
        => controller.SetDesired(desired);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        controller.Dispose();
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs _)
        => controller.RequestReconcile();
}
