using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using dad.Models;

namespace dad.Services;

internal readonly record struct DadTitleMenuReadinessSnapshot(
    DateTime CapturedAtUtc,
    DadTitleSurface Surface,
    bool ClientLoggedOut,
    bool NoActiveConditionFlags,
    bool TitleMenuReady,
    bool MovieStaffListReady,
    bool NavigationOverlayVisible,
    bool ConnectionOverlayVisible,
    bool DialogOverlayVisible)
{
    public bool IsFresh(DateTime nowUtc)
        => CapturedAtUtc != DateTime.MinValue &&
           nowUtc >= CapturedAtUtc &&
           nowUtc - CapturedAtUtc <= TimeSpan.FromSeconds(2);
}

/// <summary>
/// Captures exclusive title/lobby UI evidence on the framework thread. Wake requests can arrive on
/// a transport thread, so the takeover target consumes only this short-lived immutable snapshot.
/// Exact MovieStaffList dismissal is queued back to this same framework thread as one Escape press.
/// </summary>
public sealed unsafe class DadTitleMenuReadinessService : IDisposable
{
    private const int EscapeVirtualKey = 0x1B;
    private static readonly ConditionFlag[] ConditionFlags = Enum.GetValues<ConditionFlag>();
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IKeyState keyState;
    private readonly object gate = new();
    private DadTitleMenuReadinessSnapshot snapshot;
    private MovieEscapeState movieEscapeState;
    private bool disposed;

    public DadTitleMenuReadinessService(
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IKeyState keyState)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.keyState = keyState;
        framework.Update += OnFrameworkUpdate;
    }

    internal DadTitleMenuReadinessSnapshot Current
    {
        get
        {
            lock (gate)
                return snapshot;
        }
    }

    internal DadWakeTakeoverActionResult QueueExactMovieStaffListEscape()
    {
        lock (gate)
        {
            if (disposed)
                return DadWakeTakeoverActionResult.Rejected("Title-surface service is disposed.");
            if (!snapshot.IsFresh(DateTime.UtcNow) ||
                snapshot.Surface != DadTitleSurface.TitleMovie ||
                !snapshot.MovieStaffListReady)
            {
                return DadWakeTakeoverActionResult.Rejected(
                    "Fresh exclusive exact MovieStaffList evidence was lost before Escape could be queued.");
            }
            if (movieEscapeState != MovieEscapeState.None)
                return DadWakeTakeoverActionResult.Rejected("An exact MovieStaffList Escape is already queued.");

            movieEscapeState = MovieEscapeState.PressPending;
            return DadWakeTakeoverActionResult.Accepted();
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var captured = Capture(DateTime.UtcNow);
        var pressEscape = false;
        var releaseEscape = false;
        lock (gate)
        {
            snapshot = captured;
            switch (movieEscapeState)
            {
                case MovieEscapeState.PressPending:
                    movieEscapeState = MovieEscapeState.None;
                    if (captured.Surface == DadTitleSurface.TitleMovie &&
                        captured.MovieStaffListReady)
                    {
                        pressEscape = true;
                        movieEscapeState = MovieEscapeState.ReleasePending;
                    }
                    break;
                case MovieEscapeState.ReleasePending:
                    movieEscapeState = MovieEscapeState.None;
                    releaseEscape = true;
                    break;
            }
        }

        try
        {
            if (pressEscape)
                keyState[EscapeVirtualKey] = true;
            else if (releaseEscape)
                keyState[EscapeVirtualKey] = false;
        }
        catch
        {
            try
            {
                keyState[EscapeVirtualKey] = false;
            }
            catch
            {
                // The one-shot was already consumed. A later takeover poll remains fail-closed.
            }
        }
    }

    private DadTitleMenuReadinessSnapshot Capture(DateTime capturedAtUtc)
    {
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager == null)
                return Unreadable(capturedAtUtc);

            var title = manager->GetAddonByName("_TitleMenu");
            var movie = manager->GetAddonByName("MovieStaffList");
            var navigation = IsVisible(manager->GetAddonByName("TitleDCWorldMap"));
            var connecting = IsVisible(manager->GetAddonByName("TitleConnect"));
            var dialog = IsVisible(manager->GetAddonByName("Dialogue")) ||
                         IsVisible(manager->GetAddonByName("SelectOk")) ||
                         IsVisible(manager->GetAddonByName("SelectYesno")) ||
                         IsVisible(manager->GetAddonByName("SelectString"));
            var characterSelect =
                IsVisible(manager->GetAddonByName("_CharaSelectListMenu")) ||
                IsVisible(manager->GetAddonByName("_CharaSelectWorldServer")) ||
                IsVisible(manager->GetAddonByName("_CharaSelectReturn")) ||
                IsVisible(manager->GetAddonByName("_CharaSelectCharacterList")) ||
                IsVisible(manager->GetAddonByName("_CharaSelectNamePlate"));
            var surface = DadTitleSurfaceRules.Classify(new DadTitleSurfaceSignals(
                IsVisible(title),
                IsVisible(movie),
                connecting,
                characterSelect,
                navigation,
                dialog));
            var titleReady = title != null &&
                             title->IsReady &&
                             title->IsVisible &&
                             title->UldManager.NodeList != null &&
                             title->UldManager.NodeListCount > 7 &&
                             title->UldManager.NodeList[3] != null &&
                             title->UldManager.NodeList[7] != null &&
                             title->UldManager.NodeList[7]->IsVisible() &&
                             title->UldManager.NodeList[3]->Color.A == byte.MaxValue;
            var movieReady = movie != null && movie->IsReady && movie->IsVisible;
            return new DadTitleMenuReadinessSnapshot(
                capturedAtUtc,
                surface,
                !clientState.IsLoggedIn,
                !HasAnyActiveCondition(),
                titleReady,
                movieReady,
                navigation,
                connecting,
                dialog);
        }
        catch
        {
            return Unreadable(capturedAtUtc);
        }
    }

    private bool HasAnyActiveCondition()
    {
        foreach (var flag in ConditionFlags)
        {
            if (condition[flag])
                return true;
        }
        return false;
    }

    private static DadTitleMenuReadinessSnapshot Unreadable(DateTime capturedAtUtc)
        => new(
            capturedAtUtc,
            DadTitleSurface.Ambiguous,
            false,
            false,
            false,
            false,
            false,
            false,
            false);

    private static bool IsVisible(AtkUnitBase* addon)
        => addon != null && addon->IsVisible;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            movieEscapeState = MovieEscapeState.None;
        }
        framework.Update -= OnFrameworkUpdate;
        try
        {
            keyState[EscapeVirtualKey] = false;
        }
        catch
        {
            // Best-effort key release during plugin disposal.
        }
    }

    private enum MovieEscapeState
    {
        None,
        PressPending,
        ReleasePending,
    }
}
