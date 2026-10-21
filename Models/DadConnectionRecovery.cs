namespace dad.Models;

public sealed record DadConnectionRecoveryIdentity(
    string AccountId, string CharacterName, string HomeWorld, string CurrentWorld,
    uint CurrentDataCenterId, ulong ContentId);

public enum DadConnectionRecoverySurface
{
    None, World, Dialogue, LobbyError, Title, DataCenter, ServiceAccount,
    CharacterSelect, WorldSelect, LoginConfirmation, Connecting, OtherDialog,
}

public enum DadConnectionRecoveryAction
{
    None, DismissError, OpenDataCenter, SelectDataCenter, SelectCharacter, SelectWorld, ConfirmLogin,
}

public readonly record struct DadConnectionRecoveryObservation(
    DadConnectionRecoverySurface Surface, bool Ready, nint AddonAddress = 0);

// In-memory recovery of an observed local session. It never restores or starts DAD work.
public sealed class DadConnectionRecovery
{
    private DadConnectionRecoveryIdentity? lastSession;
    private DateTime nextActionUtc;
    private int pendingError;
    public DadConnectionRecoveryIdentity? Target { get; private set; }
    public bool IsActive => Target != null;
    public bool RequiresLoginOwnershipCheck => IsActive || System.Threading.Volatile.Read(ref pendingError) > 0;
    public string Status { get; private set; } = "Waiting for a local session.";

    public void ObserveError(int code)
    {
        if (code > 0) System.Threading.Interlocked.Exchange(ref pendingError, code);
    }

    public void ObserveLogout(int code)
    {
        if (code is 90001 or 90002 or 90006 or 90007) ObserveError(code);
        else if (code == 0x3E80 && (Target != null || System.Threading.Volatile.Read(ref pendingError) > 0)) return;
        else Cancel("Logged out normally.", forgetSession: true);
    }

    public void Cancel(string reason, bool forgetSession = false)
    {
        Target = null;
        pendingError = 0;
        nextActionUtc = default;
        Status = reason;
        if (forgetSession) lastSession = null;
    }

    public DadConnectionRecoveryAction Update(bool enabled, string accountId,
        DadConnectionRecoveryIdentity? loggedInSession, bool shiftHeld, bool anotherLoginOwner,
        DadConnectionRecoveryObservation observation, DateTime nowUtc)
    {
        if (!enabled)
        {
            Cancel("Connection recovery is off.", forgetSession: true);
            return DadConnectionRecoveryAction.None;
        }
        if (shiftHeld)
        {
            Cancel("Connection recovery stopped with Shift.");
            return DadConnectionRecoveryAction.None;
        }
        if (loggedInSession != null)
        {
            // Native lobby errors can precede both a final logged-in frame and the
            // Logout event, whose code has already been rewritten by the handler.
            if (System.Threading.Volatile.Read(ref pendingError) > 0 &&
                lastSession == loggedInSession && loggedInSession.AccountId == accountId)
                return DadConnectionRecoveryAction.None;
            Cancel(Target == loggedInSession ? "Connection recovered." : "Local session observed.");
            lastSession = loggedInSession.AccountId == accountId && loggedInSession.ContentId != 0 &&
                          !string.IsNullOrWhiteSpace(loggedInSession.CharacterName) &&
                          !string.IsNullOrWhiteSpace(loggedInSession.HomeWorld) &&
                          !string.IsNullOrWhiteSpace(loggedInSession.CurrentWorld) &&
                          loggedInSession.CurrentDataCenterId != 0 ? loggedInSession : null;
            return DadConnectionRecoveryAction.None;
        }
        if (lastSession?.AccountId != accountId)
        {
            Cancel("Connection recovery requires the same local account.", forgetSession: true);
            return DadConnectionRecoveryAction.None;
        }
        if (anotherLoginOwner)
        {
            // An existing wake or external login remains the sole owner. Do not wake later
            // from this abandoned recovery after that owner finishes or is cancelled.
            Cancel("Connection recovery yielded to another login owner.", forgetSession: true);
            return DadConnectionRecoveryAction.None;
        }
        var error = System.Threading.Interlocked.Exchange(ref pendingError, 0);
        if (error > 0)
        {
            if (Target == null) Target = lastSession;
            Status = $"Recovering connection after error {error}.";
        }
        if (Target != null && observation.Surface == DadConnectionRecoverySurface.LobbyError)
            Status = "Close the visible lobby error to continue connection recovery.";
        if (Target == null || nowUtc < nextActionUtc || !observation.Ready)
            return DadConnectionRecoveryAction.None;

        var action = observation.Surface switch
        {
            DadConnectionRecoverySurface.Dialogue => DadConnectionRecoveryAction.DismissError,
            DadConnectionRecoverySurface.Title => DadConnectionRecoveryAction.OpenDataCenter,
            DadConnectionRecoverySurface.DataCenter => DadConnectionRecoveryAction.SelectDataCenter,
            DadConnectionRecoverySurface.CharacterSelect => DadConnectionRecoveryAction.SelectCharacter,
            DadConnectionRecoverySurface.WorldSelect => DadConnectionRecoveryAction.SelectWorld,
            DadConnectionRecoverySurface.LoginConfirmation => DadConnectionRecoveryAction.ConfirmLogin,
            _ => DadConnectionRecoveryAction.None,
        };
        if (action != DadConnectionRecoveryAction.None)
            nextActionUtc = nowUtc.AddMilliseconds(action == DadConnectionRecoveryAction.DismissError ? 500 : 150);
        else if (observation.Surface == DadConnectionRecoverySurface.ServiceAccount)
            Status = "Connection recovery is waiting for your service-account selection.";
        return action;
    }

    // The lobby handler receives a tagged native value; preserve empty/non-numeric values.
    public static bool ProtectLobbyError(Span<byte> value, bool enabled, out int originalCode)
    {
        originalCode = 0;
        if (!enabled || value.Length < 16 || (value[0] & 0xF) == 0) return false;
        var code = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value[8..]);
        if (code == 0) return false;
        originalCode = unchecked((int)code);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(value[8..], 0x3E80);
        return true;
    }
}
