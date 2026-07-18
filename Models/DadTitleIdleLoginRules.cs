namespace dad.Models;

public static class DadTitleIdleLoginRules
{
    public static bool CanProveAutoRetainerOwnership(DadWakeTakeoverTargetSnapshot snapshot)
        => HasExactStableRoute(snapshot) &&
           HasReadyIdleTitle(snapshot) &&
           HasReadableIdleAutoRetainer(snapshot) &&
           snapshot.MultiModeEnabled &&
           HasNoConflictingOwnership(snapshot);

    public static bool CanContinueOwnedAttempt(DadWakeTakeoverTargetSnapshot snapshot)
        => HasExactStableRoute(snapshot) &&
           HasReadyIdleTitle(snapshot) &&
           HasReadableIdleAutoRetainer(snapshot) &&
           HasNoConflictingOwnership(snapshot);

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
        if (!snapshot.TitleMenuEvidenceFresh || !snapshot.TitleMenuReady)
            return "AutoRetainer title login is waiting for fresh ready _TitleMenu evidence; no command was issued.";
        if (snapshot.TitleNavigationOverlayVisible)
            return "AutoRetainer title login is waiting for title navigation to close; no command was issued.";
        if (snapshot.TitleConnectionOverlayVisible)
            return "AutoRetainer title login is waiting for the title connection overlay to close; no command was issued.";
        if (snapshot.TitleErrorOverlayVisible)
            return "AutoRetainer title login is waiting for the title error/dialog overlay to close; no command was issued.";
        if (!snapshot.AutoRetainerAvailable)
            return "AutoRetainer title login is waiting for readable AutoRetainer IPC; no command was issued.";
        if (snapshot.AutoRetainerBusy)
            return "AutoRetainer title login is waiting for AutoRetainer to become idle; no command was issued.";
        if (!snapshot.SuppressionReadable)
            return "AutoRetainer title login is waiting for readable ownership state; no command was issued.";
        if (snapshot.ExternalAutomationHeld || snapshot.AutoRetainerSuppressed ||
            snapshot.DadOwnsSuppression || snapshot.DadOwnsCharacterPostprocess)
        {
            return "AutoRetainer title login is waiting for conflicting automation ownership to clear; no command was issued.";
        }
        return "AutoRetainer title login is waiting for an exact safe title-idle state; no command was issued.";
    }

    private static bool HasReadyIdleTitle(DadWakeTakeoverTargetSnapshot snapshot)
        => !snapshot.Participant.IsAvailable &&
           snapshot.TitleMenuEvidenceFresh &&
           snapshot.TitleMenuReady &&
           !snapshot.TitleNavigationOverlayVisible &&
           !snapshot.TitleConnectionOverlayVisible &&
           !snapshot.TitleErrorOverlayVisible;

    private static bool HasReadableIdleAutoRetainer(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.AutoRetainerAvailable &&
           !snapshot.AutoRetainerBusy &&
           snapshot.SuppressionReadable;

    private static bool HasNoConflictingOwnership(DadWakeTakeoverTargetSnapshot snapshot)
        => !snapshot.ExternalAutomationHeld &&
           !snapshot.AutoRetainerSuppressed &&
           !snapshot.DadOwnsSuppression &&
           !snapshot.DadOwnsCharacterPostprocess;
}
