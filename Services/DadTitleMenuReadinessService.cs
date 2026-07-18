using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

public readonly record struct DadTitleMenuReadinessSnapshot(
    DateTime CapturedAtUtc,
    bool TitleMenuReady,
    bool NavigationOverlayVisible,
    bool ConnectionOverlayVisible,
    bool ErrorOverlayVisible)
{
    public bool IsFresh(DateTime nowUtc)
        => CapturedAtUtc != DateTime.MinValue &&
           nowUtc >= CapturedAtUtc &&
           nowUtc - CapturedAtUtc <= TimeSpan.FromSeconds(2);
}

/// <summary>
/// Captures title-menu UI evidence on the framework thread. Wake requests can arrive on a
/// transport thread, so the takeover target consumes only this short-lived immutable snapshot.
/// </summary>
public sealed unsafe class DadTitleMenuReadinessService : IDisposable
{
    private readonly IFramework framework;
    private readonly object gate = new();
    private DadTitleMenuReadinessSnapshot snapshot;
    private bool disposed;

    public DadTitleMenuReadinessService(IFramework framework)
    {
        this.framework = framework;
        framework.Update += OnFrameworkUpdate;
    }

    public DadTitleMenuReadinessSnapshot Current
    {
        get
        {
            lock (gate)
                return snapshot;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var captured = Capture(DateTime.UtcNow);
        lock (gate)
            snapshot = captured;
    }

    private static DadTitleMenuReadinessSnapshot Capture(DateTime capturedAtUtc)
    {
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager == null)
                return new DadTitleMenuReadinessSnapshot(capturedAtUtc, false, false, false, false);

            var title = manager->GetAddonByName("_TitleMenu");
            var titleReady = title != null &&
                             title->IsReady &&
                             title->IsVisible &&
                             title->UldManager.NodeList != null &&
                             title->UldManager.NodeListCount > 7 &&
                             title->UldManager.NodeList[3] != null &&
                             title->UldManager.NodeList[7] != null &&
                             title->UldManager.NodeList[7]->IsVisible() &&
                             title->UldManager.NodeList[3]->Color.A == byte.MaxValue;
            return new DadTitleMenuReadinessSnapshot(
                capturedAtUtc,
                titleReady,
                IsVisibleAndReady(manager->GetAddonByName("TitleDCWorldMap")),
                IsVisibleAndReady(manager->GetAddonByName("TitleConnect")),
                IsVisibleAndReady(manager->GetAddonByName("Dialogue")) ||
                IsVisibleAndReady(manager->GetAddonByName("SelectOk")) ||
                IsVisibleAndReady(manager->GetAddonByName("SelectYesno")));
        }
        catch
        {
            return new DadTitleMenuReadinessSnapshot(capturedAtUtc, false, false, false, false);
        }
    }

    private static bool IsVisibleAndReady(AtkUnitBase* addon)
        => addon != null && addon->IsReady && addon->IsVisible;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
    }
}
