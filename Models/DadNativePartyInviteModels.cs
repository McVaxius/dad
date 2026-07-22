namespace dad.Models;

public enum DadNativePartyInviteType
{
    SameWorld,
    CrossWorldContentId,
    InInstanceContentId,
}

public sealed class DadNativePartyInviteTarget
{
    public string RunId { get; init; } = string.Empty;
    public DadModuleId ModuleId { get; init; } = DadModuleId.None;
    public string SlotId { get; init; } = string.Empty;
    public DadAccountKey AccountKey { get; init; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; init; } = new(string.Empty);
    public ulong ContentId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public ushort WorldId { get; init; }
    public DadWorkerSessionId WorkerSessionId { get; init; } = new(string.Empty);
    public uint LocalCurrentWorldId { get; init; }
    public bool WorldRelationExact { get; init; }
    public bool SameApplicableInstanceExact { get; init; }

    public DadNativePartyInviteTarget Clone()
        => new()
        {
            RunId = RunId,
            ModuleId = ModuleId,
            SlotId = SlotId,
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            CharacterName = CharacterName,
            WorldId = WorldId,
            WorkerSessionId = WorkerSessionId,
            LocalCurrentWorldId = LocalCurrentWorldId,
            WorldRelationExact = WorldRelationExact,
            SameApplicableInstanceExact = SameApplicableInstanceExact,
        };
}

public sealed class DadNativePartyInviteAttempt
{
    public DadNativePartyInviteType InviteType { get; init; }
    public int AttemptNumber { get; init; }
    public bool DispatchResult { get; init; }
    public bool PartyListContainsContentId { get; init; }
    public DateTime AttemptedAtUtc { get; init; }
    public DateTime NextAttemptAtUtc { get; init; }
}

public interface IDadNativePartyInviteDispatcher
{
    bool InviteSameWorld(ulong contentId, string exactCharacterName, ushort worldId);

    bool InviteCrossWorld(ulong contentId, ushort worldId);

    bool InviteInInstance(ulong contentId);
}

public static class DadNativePartyInviteRules
{
    public static DadNativePartyInviteType SelectInviteType(
        DadNativePartyInviteTarget target,
        int attemptNumber)
    {
        if (target.SameApplicableInstanceExact)
            return DadNativePartyInviteType.InInstanceContentId;

        var likelyType = target.LocalCurrentWorldId != 0 &&
                         target.WorldId != 0 &&
                         target.LocalCurrentWorldId == target.WorldId
            ? DadNativePartyInviteType.SameWorld
            : DadNativePartyInviteType.CrossWorldContentId;
        if (target.WorldRelationExact || attemptNumber % 2 == 1)
            return likelyType;

        return likelyType == DadNativePartyInviteType.SameWorld
            ? DadNativePartyInviteType.CrossWorldContentId
            : DadNativePartyInviteType.SameWorld;
    }
}

public sealed class DadNativePartyInviteAttemptTracker
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<ulong, TargetAttemptState> targets = [];
    private string activeRunId = string.Empty;
    private bool runConfirmed;

    public void BeginRun(string runId)
    {
        runId = Normalize(runId);
        if (Same(activeRunId, runId))
            return;

        targets.Clear();
        activeRunId = runId;
        runConfirmed = false;
    }

    public DadNativePartyInviteAttempt? TryDispatch(
        DadNativePartyInviteTarget target,
        bool partyListContainsContentId,
        DateTime nowUtc,
        IDadNativePartyInviteDispatcher dispatcher,
        out string blocker)
    {
        blocker = Validate(target);
        if (!string.IsNullOrWhiteSpace(blocker))
            return null;

        BeginRun(target.RunId);
        if (runConfirmed)
            return null;

        nowUtc = EnsureUtc(nowUtc);
        if (!targets.TryGetValue(target.ContentId, out var state))
        {
            state = new TargetAttemptState { FrozenTarget = target.Clone() };
            targets[target.ContentId] = state;
        }
        else if (!SameFrozenIdentity(state.FrozenTarget, target))
        {
            blocker = $"Native party invite target identity changed after it was frozen for {target.SlotId}.";
            return null;
        }

        if (partyListContainsContentId)
        {
            state.PartyConfirmed = true;
            return null;
        }

        if (state.PartyConfirmed || nowUtc < state.NextAttemptAtUtc)
            return null;

        state.AttemptNumber++;
        var inviteType = DadNativePartyInviteRules.SelectInviteType(state.FrozenTarget, state.AttemptNumber);
        var dispatchResult = inviteType switch
        {
            DadNativePartyInviteType.SameWorld => dispatcher.InviteSameWorld(
                state.FrozenTarget.ContentId,
                state.FrozenTarget.CharacterName,
                state.FrozenTarget.WorldId),
            DadNativePartyInviteType.CrossWorldContentId => dispatcher.InviteCrossWorld(
                state.FrozenTarget.ContentId,
                0),
            DadNativePartyInviteType.InInstanceContentId => dispatcher.InviteInInstance(
                state.FrozenTarget.ContentId),
            _ => false,
        };

        state.NextAttemptAtUtc = nowUtc + RetryInterval;
        return new DadNativePartyInviteAttempt
        {
            InviteType = inviteType,
            AttemptNumber = state.AttemptNumber,
            DispatchResult = dispatchResult,
            PartyListContainsContentId = partyListContainsContentId,
            AttemptedAtUtc = nowUtc,
            NextAttemptAtUtc = state.NextAttemptAtUtc,
        };
    }

    public bool ConfirmRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(activeRunId))
            BeginRun(runId);
        if (!Same(activeRunId, runId) || runConfirmed)
            return false;

        runConfirmed = true;
        foreach (var state in targets.Values)
            state.PartyConfirmed = true;
        return true;
    }

    public void Clear()
    {
        targets.Clear();
        activeRunId = string.Empty;
        runConfirmed = false;
    }

    private static string Validate(DadNativePartyInviteTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.RunId))
            return "Native party invite is missing its run id.";
        if (string.IsNullOrWhiteSpace(target.SlotId) || target.AccountKey.IsEmpty || target.CharacterKey.IsEmpty)
            return "Native party invite is missing its frozen slot, account, or character identity.";
        if (target.ContentId == 0 || target.WorkerSessionId.IsEmpty)
            return "Native party invite requires a frozen Content ID and worker session.";
        if (string.IsNullOrWhiteSpace(target.CharacterName) || target.WorldId == 0)
            return "Native party invite requires the exact character name and World ID.";
        return string.Empty;
    }

    private static bool SameFrozenIdentity(DadNativePartyInviteTarget left, DadNativePartyInviteTarget right)
        => Same(left.RunId, right.RunId) &&
           left.ModuleId == right.ModuleId &&
           Same(left.SlotId, right.SlotId) &&
           Same(left.AccountKey.Value, right.AccountKey.Value) &&
           Same(left.CharacterKey.Value, right.CharacterKey.Value) &&
           left.ContentId == right.ContentId &&
           string.Equals(left.CharacterName, right.CharacterName, StringComparison.Ordinal) &&
           left.WorldId == right.WorldId &&
           Same(left.WorkerSessionId.Value, right.WorkerSessionId.Value);

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class TargetAttemptState
    {
        public DadNativePartyInviteTarget FrozenTarget { get; init; } = new();
        public int AttemptNumber { get; set; }
        public DateTime NextAttemptAtUtc { get; set; } = DateTime.MinValue;
        public bool PartyConfirmed { get; set; }
    }
}

public readonly record struct DadPendingPartyInvitation(
    uint InviteTime,
    string InviterName,
    ushort InviterWorldId)
{
    public bool IsPresent => InviteTime != 0 && !string.IsNullOrEmpty(InviterName);
}

internal readonly record struct DadSelectYesnoPromptSnapshot(
    bool Visible,
    string Identity,
    string Text);

internal static class DadPartyInvitePromptOwnershipRules
{
    public static bool ShouldRestoreHiddenPrompt(
        DadPendingPartyInvitation invitation,
        DadExpectedPartyInviter expected,
        DadSelectYesnoPromptSnapshot prompt)
        => IsExactPendingInvitation(invitation, expected) && !prompt.Visible;

    public static bool CanUseDirectYes(
        DadPendingPartyInvitation beforeInvitation,
        DadPendingPartyInvitation afterInvitation,
        DadExpectedPartyInviter expected,
        DadSelectYesnoPromptSnapshot runBaselinePrompt,
        DadSelectYesnoPromptSnapshot beforePrompt,
        DadSelectYesnoPromptSnapshot afterPrompt,
        bool restoreDispatched)
    {
        if (!afterPrompt.Visible ||
            beforeInvitation != afterInvitation ||
            !IsExactPendingInvitation(afterInvitation, expected))
        {
            return false;
        }

        if (runBaselinePrompt.Visible &&
            string.Equals(runBaselinePrompt.Identity, afterPrompt.Identity, StringComparison.Ordinal))
        {
            return false;
        }

        var newlySurfacedByDad = restoreDispatched && !beforePrompt.Visible && afterPrompt.Visible;
        return newlySurfacedByDad || PromptProvesExpectedInviter(afterPrompt.Text, expected.CharacterName);
    }

    public static bool IsExactPendingInvitation(
        DadPendingPartyInvitation invitation,
        DadExpectedPartyInviter expected)
        => invitation.IsPresent &&
           string.Equals(invitation.InviterName, expected.CharacterName, StringComparison.Ordinal) &&
           invitation.InviterWorldId == expected.WorldId;

    private static bool PromptProvesExpectedInviter(string promptText, string exactInviterName)
        => !string.IsNullOrWhiteSpace(promptText) &&
           !string.IsNullOrWhiteSpace(exactInviterName) &&
           promptText.Contains(exactInviterName, StringComparison.Ordinal) &&
           promptText.Contains("party", StringComparison.OrdinalIgnoreCase);
}

public sealed class DadExpectedPartyInviter
{
    public string RunId { get; init; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; init; } = new(string.Empty);
    public DadAccountKey AccountKey { get; init; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; init; } = new(string.Empty);
    public ulong ContentId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public ushort WorldId { get; init; }
}

public sealed class DadPartyInvitationAcceptanceTracker
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private string activeRunId = string.Empty;
    private DadPendingPartyInvitation baseline;
    private DadPendingPartyInvitation lastAttemptedInvitation;
    private DadExpectedPartyInviter? expectedInviter;
    private DateTime nextAttemptAtUtc = DateTime.MinValue;
    private DateTime? firstRealAttemptAtUtc;
    private bool partyConfirmed;

    public DadExpectedPartyInviter? ExpectedInviter => expectedInviter;

    public void BeginRun(string runId, DadPendingPartyInvitation baselineInvitation)
    {
        runId = Normalize(runId);
        if (Same(activeRunId, runId))
            return;

        activeRunId = runId;
        baseline = baselineInvitation;
        lastAttemptedInvitation = default;
        expectedInviter = null;
        nextAttemptAtUtc = DateTime.MinValue;
        firstRealAttemptAtUtc = null;
        partyConfirmed = false;
    }

    public bool TryArm(DadExpectedPartyInviter inviter, out string blocker)
    {
        blocker = Validate(inviter);
        if (!string.IsNullOrWhiteSpace(blocker))
            return false;
        if (!Same(activeRunId, inviter.RunId))
        {
            blocker = $"Party invitation acceptance is armed for run '{activeRunId}', not '{inviter.RunId}'.";
            return false;
        }

        if (expectedInviter == null)
        {
            expectedInviter = inviter;
            return true;
        }

        if (SameExpectedInviter(expectedInviter, inviter))
            return true;

        blocker = "Party invitation inviter identity changed after it was frozen for this run.";
        return false;
    }

    public bool ShouldAccept(
        DadPendingPartyInvitation invitation,
        bool partyListContainsExpectedContentId,
        DateTime nowUtc)
    {
        if (partyListContainsExpectedContentId)
        {
            ConfirmPartyMembership();
            return false;
        }

        if (partyConfirmed || expectedInviter == null || !invitation.IsPresent)
            return false;
        if (invitation == baseline)
            return false;
        if (!string.Equals(invitation.InviterName, expectedInviter.CharacterName, StringComparison.Ordinal) ||
            invitation.InviterWorldId != expectedInviter.WorldId)
        {
            return false;
        }

        nowUtc = EnsureUtc(nowUtc);
        return invitation != lastAttemptedInvitation || nowUtc >= nextAttemptAtUtc;
    }

    public void RecordAttempt(DadPendingPartyInvitation invitation, DateTime attemptedAtUtc)
    {
        firstRealAttemptAtUtc ??= EnsureUtc(attemptedAtUtc);
        lastAttemptedInvitation = invitation;
        nextAttemptAtUtc = EnsureUtc(attemptedAtUtc) + RetryInterval;
    }

    internal string BuildRetryStatus(DateTime nowUtc, TimeSpan assemblyWindow)
    {
        if (expectedInviter == null)
            return string.Empty;

        nowUtc = EnsureUtc(nowUtc);
        var window = assemblyWindow <= TimeSpan.Zero ? TimeSpan.FromSeconds(120) : assemblyWindow;
        if (firstRealAttemptAtUtc.HasValue && nowUtc - firstRealAttemptAtUtc.Value >= window)
            return DadPartyInvitationRetryRules.BuildWarning(expectedInviter);

        return DadPartyInvitationRetryRules.BuildActive(expectedInviter);
    }

    public bool ConfirmPartyMembership()
    {
        if (partyConfirmed)
            return false;

        partyConfirmed = true;
        return true;
    }

    public void Clear()
    {
        activeRunId = string.Empty;
        baseline = default;
        lastAttemptedInvitation = default;
        expectedInviter = null;
        nextAttemptAtUtc = DateTime.MinValue;
        firstRealAttemptAtUtc = null;
        partyConfirmed = false;
    }

    internal static string Validate(DadExpectedPartyInviter inviter)
    {
        if (string.IsNullOrWhiteSpace(inviter.RunId) || inviter.WorkerSessionId.IsEmpty)
            return "Party invitation acceptance requires a run and inviter worker session.";
        if (inviter.AccountKey.IsEmpty || inviter.CharacterKey.IsEmpty || inviter.ContentId == 0)
            return "Party invitation acceptance requires the frozen inviter account, character, and Content ID.";
        if (string.IsNullOrEmpty(inviter.CharacterName) || inviter.WorldId == 0)
            return "Party invitation acceptance requires the inviter's exact name and World ID.";
        return string.Empty;
    }

    internal static bool SameExpectedInviter(DadExpectedPartyInviter left, DadExpectedPartyInviter right)
        => Same(left.RunId, right.RunId) &&
           Same(left.WorkerSessionId.Value, right.WorkerSessionId.Value) &&
           Same(left.AccountKey.Value, right.AccountKey.Value) &&
           Same(left.CharacterKey.Value, right.CharacterKey.Value) &&
           left.ContentId == right.ContentId &&
           string.Equals(left.CharacterName, right.CharacterName, StringComparison.Ordinal) &&
           left.WorldId == right.WorldId;

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

internal static class DadPartyInvitationRetryRules
{
    private const string ActivePrefix = "Party invitation retry active:";
    private const string WarningPrefix = "Party invitation warning:";

    public static string BuildActive(DadExpectedPartyInviter expected)
        => $"{ActivePrefix} waiting for a fresh exact invite from {expected.CharacterKey}; Dad remains reachable and restore/Yes retries continue every five seconds.";

    public static string BuildWarning(DadExpectedPartyInviter expected)
        => $"{WarningPrefix} this participant has not accepted the exact invite from {expected.CharacterKey}; Dad remains reachable and restore/Yes retries continue every five seconds.";

    public static string BuildWarning(DadCharacterKey participant, DadCharacterKey expectedInviter)
        => $"{WarningPrefix} participant {participant} has not accepted the exact invite from {expectedInviter}; Dad remains reachable and restore/Yes retries continue every five seconds.";

    public static bool IsContinuingRetry(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.StartsWith(ActivePrefix, StringComparison.Ordinal) ||
            value.StartsWith(WarningPrefix, StringComparison.Ordinal));

    public static bool IsPersistentWarning(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith(WarningPrefix, StringComparison.Ordinal);

    public static bool ShouldApplyAssemblyTimeout(
        bool persistentStartup,
        bool timedOut,
        IEnumerable<string> blockers)
        => !persistentStartup &&
           timedOut &&
           !blockers.Any(IsContinuingRetry);
}
