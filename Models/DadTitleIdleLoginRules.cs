namespace dad.Models;

public static class DadTitleIdleLoginRules
{
    public static bool CanProveAutoRetainerOwnership(DadWakeTakeoverTargetSnapshot snapshot)
        => HasExactStableRoute(snapshot) &&
           HasReadyIdleTitle(snapshot) &&
           HasReadableIdleAutoRetainer(snapshot) &&
           HasFreshIdleVermaxion(snapshot) &&
           HasReadyLifestreamLogin(snapshot) &&
           HasNoConflictingOwnership(snapshot);

    public static bool CanContinueOwnedAttempt(DadWakeTakeoverTargetSnapshot snapshot)
        => HasExactStableRoute(snapshot) &&
           HasReadyIdleTitle(snapshot) &&
           HasReadableIdleAutoRetainer(snapshot) &&
           HasFreshIdleVermaxion(snapshot) &&
           HasReadyLifestreamLogin(snapshot) &&
           HasNoConflictingOwnership(snapshot);

    internal static bool CanDismissExactTitleMovie(DadWakeTakeoverTargetSnapshot snapshot)
        => HasExactStableRoute(snapshot) &&
           !snapshot.Participant.IsAvailable &&
           snapshot.TitleSurfaceEvidenceFresh &&
           snapshot.TitleSurface == DadTitleSurface.TitleMovie &&
           snapshot.TitleMovieExactReady &&
           snapshot.TitleClientLoggedOut &&
           snapshot.TitleNoActiveConditionFlags &&
           HasReadableIdleAutoRetainer(snapshot) &&
           HasFreshIdleVermaxion(snapshot) &&
           snapshot.LifestreamAvailable &&
           !snapshot.LifestreamBusy &&
           !snapshot.MultiModeEnabled &&
           HasNoConflictingOwnership(snapshot);

    internal static bool IsTitleTakeoverSurface(DadWakeTakeoverTargetSnapshot snapshot)
        => !snapshot.Participant.IsAvailable &&
           snapshot.TitleSurfaceEvidenceFresh &&
           snapshot.TitleSurface is DadTitleSurface.TitleMenu or DadTitleSurface.TitleMovie;

    internal static bool HasExactPostDisableSurface(DadWakeTakeoverTargetSnapshot snapshot)
        => HasExactStableRoute(snapshot) &&
           !snapshot.Participant.IsAvailable &&
           snapshot.TitleSurfaceEvidenceFresh &&
           snapshot.TitleSurface == DadTitleSurface.TitleMenu;

    public static bool HasExactStableRoute(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.ClientRouteConnected &&
           snapshot.AccountMatches &&
           snapshot.CharacterKnownToAccount;

    public static string BuildWaitSummary(DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!snapshot.ClientRouteConnected)
            return "AutoRetainer title login is waiting for the frozen connected DAD route; no command was issued.";
        if (!snapshot.AccountMatches)
            return "AutoRetainer title login is waiting for the exact configured stable-account route; no command was issued.";
        if (!snapshot.CharacterKnownToAccount)
            return "AutoRetainer title login is waiting for exact configured character catalog truth; no command was issued.";
        if (snapshot.Participant.IsAvailable)
            return "AutoRetainer title login observed a local character instead of an idle title screen; no title command was issued.";
        if (!snapshot.TitleSurfaceEvidenceFresh)
            return "AutoRetainer title login is waiting for fresh exclusive title-surface evidence; no command was issued.";
        if (!snapshot.TitleClientLoggedOut)
            return "AutoRetainer title login is waiting for independent logged-out proof; no command was issued.";
        if (!snapshot.TitleNoActiveConditionFlags)
            return "AutoRetainer title login is waiting for all condition flags to clear; no command was issued.";
        switch (snapshot.TitleSurface)
        {
            case DadTitleSurface.CharacterSelect:
                return "AutoRetainer title login is waiting at Character Select; Character Select is never an eligible takeover surface.";
            case DadTitleSurface.ConnectingToDataCenter:
                return "AutoRetainer title login is waiting for TitleConnect/Data Center connection to finish; no command was issued.";
            case DadTitleSurface.TitleMovie:
                return "AutoRetainer title login is waiting for the title movie to close and a fresh valid TitleMenu; no login command was issued.";
            case DadTitleSurface.Ambiguous:
                return "AutoRetainer title login is waiting for dialogs, navigation, multiple, or unknown title surfaces to clear; no command was issued.";
            case DadTitleSurface.None:
                return "AutoRetainer title login is waiting for one recognized exclusive title surface; no command was issued.";
            case not DadTitleSurface.TitleMenu:
                return "AutoRetainer title login is waiting for an exact exclusive TitleMenu; no command was issued.";
        }
        if (!snapshot.TitleMenuReady)
            return "AutoRetainer title login is waiting for fresh ready _TitleMenu node evidence; no command was issued.";
        if (!snapshot.AutoRetainerAvailable)
            return "AutoRetainer title login is waiting for readable AutoRetainer IPC; no command was issued.";
        if (snapshot.AutoRetainerBusy)
            return "AutoRetainer title login is waiting for AutoRetainer to become idle; no command was issued.";
        if (!snapshot.SuppressionReadable)
            return "AutoRetainer title login is waiting for readable ownership state; no command was issued.";
        if (!snapshot.VermaxionStatusEvidenceFresh)
            return "AutoRetainer title login is waiting for a fresh VERMAXION status read; no command was issued.";
        if (snapshot.VermaxionStatusKind != DadVermaxionReadinessKind.Idle)
            return $"AutoRetainer title login is waiting for VERMAXION to report exactly Idle (observed {snapshot.VermaxionStatusKind}); no command was issued.";
        if (!snapshot.LifestreamAvailable)
            return "AutoRetainer title login is waiting for readable Lifestream IPC; no command was issued.";
        if (snapshot.LifestreamBusy)
            return "AutoRetainer title login is waiting for Lifestream to become idle; no command was issued.";
        if (!snapshot.LifestreamCanAutoLogin)
            return "AutoRetainer title login is waiting for fresh Lifestream CanAutoLogin proof; no command was issued.";
        if (snapshot.ExternalAutomationHeld || snapshot.AutoRetainerSuppressed ||
            snapshot.DadOwnsSuppression || snapshot.DadOwnsCharacterPostprocess)
        {
            return "AutoRetainer title login is waiting for conflicting automation ownership to clear; no command was issued.";
        }
        return "AutoRetainer title login is waiting for an exact safe title-idle state; no command was issued.";
    }

    internal static string BuildMovieWaitSummary(DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!HasExactStableRoute(snapshot))
            return BuildWaitSummary(snapshot);
        if (!snapshot.TitleSurfaceEvidenceFresh ||
            snapshot.TitleSurface != DadTitleSurface.TitleMovie ||
            !snapshot.TitleMovieExactReady)
        {
            return "Title-movie dismissal is waiting for fresh exclusive exact MovieStaffList evidence; no key was typed.";
        }
        if (!snapshot.TitleClientLoggedOut || !snapshot.TitleNoActiveConditionFlags)
            return "Title-movie dismissal is waiting for logged-out, condition-clear framework proof; no key was typed.";
        if (snapshot.MultiModeEnabled)
            return "Title-movie dismissal is waiting for AutoRetainer Multi Mode to be off; no key was typed.";
        if (!snapshot.AutoRetainerAvailable || snapshot.AutoRetainerBusy || !snapshot.SuppressionReadable)
            return "Title-movie dismissal is waiting for readable idle AutoRetainer ownership state; no key was typed.";
        if (!snapshot.VermaxionStatusEvidenceFresh ||
            snapshot.VermaxionStatusKind != DadVermaxionReadinessKind.Idle)
        {
            return "Title-movie dismissal is waiting for fresh VERMAXION Idle proof; no key was typed.";
        }
        if (!snapshot.LifestreamAvailable || snapshot.LifestreamBusy)
            return "Title-movie dismissal is waiting for readable idle Lifestream; no key was typed.";
        if (!HasNoConflictingOwnership(snapshot))
            return "Title-movie dismissal is waiting for conflicting automation ownership to clear; no key was typed.";
        return "Title-movie dismissal is waiting for exact idle safety proof; no key was typed.";
    }

    private static bool HasReadyIdleTitle(DadWakeTakeoverTargetSnapshot snapshot)
        => !snapshot.Participant.IsAvailable &&
           snapshot.TitleSurfaceEvidenceFresh &&
           snapshot.TitleSurface == DadTitleSurface.TitleMenu &&
           snapshot.TitleClientLoggedOut &&
           snapshot.TitleNoActiveConditionFlags &&
           snapshot.TitleMenuReady &&
           !snapshot.TitleNavigationOverlayVisible &&
           !snapshot.TitleConnectionOverlayVisible &&
           !snapshot.TitleErrorOverlayVisible;

    private static bool HasReadableIdleAutoRetainer(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.AutoRetainerAvailable &&
           !snapshot.AutoRetainerBusy &&
           snapshot.SuppressionReadable;

    private static bool HasFreshIdleVermaxion(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.VermaxionStatusEvidenceFresh &&
           snapshot.VermaxionStatusKind == DadVermaxionReadinessKind.Idle;

    private static bool HasReadyLifestreamLogin(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.LifestreamAvailable &&
           !snapshot.LifestreamBusy &&
           snapshot.LifestreamCanAutoLogin;

    private static bool HasNoConflictingOwnership(DadWakeTakeoverTargetSnapshot snapshot)
        => !snapshot.ExternalAutomationHeld &&
           !snapshot.AutoRetainerSuppressed &&
           !snapshot.DadOwnsSuppression &&
           !snapshot.DadOwnsCharacterPostprocess;
}
