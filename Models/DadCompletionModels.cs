namespace dad.Models;

// Feature batch A (dadfeatures20260620b): operator-chosen actions to run when a Dad run completes.
public enum DadCompletionKillMode
{
    None = 0,
    CloseGameClient = 1, // legacy setting preserved but disabled
    ShutDownPc = 2,      // legacy setting preserved but disabled
}

public sealed class DadCompletionActions
{
    // Play a game sound effect (se.1..se.16) when a run completes.
    public bool PlaySound { get; set; } = false;
    public int SoundEffectId { get; set; } = 1;

    // Run operator-supplied slash commands after completion (e.g. resume another tool).
    public bool RunCommands { get; set; } = false;
    public List<string> Commands { get; set; } = [];

    // Legacy kill actions are kept for config compatibility but disabled at runtime.
    public DadCompletionKillMode KillMode { get; set; } = DadCompletionKillMode.None;
    public DadPostRunUtilities Utilities { get; set; } = new();

    public DadCompletionActions Clone()
        => new()
        {
            PlaySound = PlaySound,
            SoundEffectId = SoundEffectId,
            RunCommands = RunCommands,
            Commands = Commands == null ? [] : [..Commands],
            KillMode = KillMode,
            Utilities = (Utilities ?? new DadPostRunUtilities()).Clone(),
        };
}

public static class DadCompletionActionSnapshots
{
    public static DadCompletionActions Resolve(DadCompletionActions? snapshot, DadCompletionActions? fallback)
        => (snapshot ?? fallback ?? new DadCompletionActions()).Clone();

    public static string DescribeSource(DadCompletionActions? snapshot)
        => snapshot == null ? "Global defaults" : "Preset override";
}

public enum DadMemberCompletionState { Pending, Submitted, Acknowledged, Uncertain, Rejected, Cancelled }

public sealed class DadMemberCompletionRecipient
{
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadAccountKey ManagedAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey ActiveCharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public string AssignedSlotId { get; set; } = string.Empty;
    public DadMemberCompletionRecipient Clone() => (DadMemberCompletionRecipient)MemberwiseClone();
    public static DadMemberCompletionRecipient From(DadParticipantSnapshot participant) => new()
    {
        WorkerSessionId = participant.WorkerSessionId, ManagedAccountKey = participant.ManagedAccountKey,
        ActiveCharacterKey = participant.ActiveCharacterKey, ContentId = participant.Character.ContentId,
        AssignedSlotId = participant.AssignedSlotId,
    };
}

public sealed class DadMemberCompletionCommand
{
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string RoundId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public DadMemberCompletionRecipient Recipient { get; set; } = new();
    public string Command { get; set; } = string.Empty;
    public DadMemberCompletionState State { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DadMemberCompletionCommand Clone()
    {
        var result = (DadMemberCompletionCommand)MemberwiseClone();
        result.Recipient = Recipient.Clone();
        return result;
    }
}

public sealed class DadRoundCompletionSnapshot
{
    public string RoundId { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public bool Cancelled { get; set; }
    public bool GlobalActionsQueued { get; set; }
    public bool ShoppingComplete { get; set; } = true;
    public Dictionary<string, bool> ShoppingCompletion { get; set; } = new(StringComparer.Ordinal);
    public List<DadMemberCompletionCommand> Members { get; set; } = [];
    public List<DadCompletionActions> GlobalActions { get; set; } = [];
}

public static class DadMemberCompletionRules
{
    public static string RoundId(DadRunRequest request)
        => string.IsNullOrWhiteSpace(request.CompletionRoundId) ? request.RequestId : request.CompletionRoundId;

    public static bool SameRecipient(DadParticipantSnapshot left, DadParticipantSnapshot right)
        => !left.ManagedAccountKey.IsEmpty && !left.ActiveCharacterKey.IsEmpty &&
           DadRosterIdentity.SameAccount(left.ManagedAccountKey, right.ManagedAccountKey) &&
           string.Equals(left.ActiveCharacterKey.Value, right.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
           left.Character.ContentId != 0 && left.Character.ContentId == right.Character.ContentId;

    public static bool SameRecipient(DadMemberCompletionRecipient left, DadMemberCompletionRecipient right)
        => !left.ManagedAccountKey.IsEmpty && !left.ActiveCharacterKey.IsEmpty &&
           DadRosterIdentity.SameAccount(left.ManagedAccountKey, right.ManagedAccountKey) &&
           string.Equals(left.ActiveCharacterKey.Value, right.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
           left.ContentId != 0 && left.ContentId == right.ContentId;

    public static bool SameRecipient(DadMemberCompletionRecipient left, DadParticipantSnapshot right)
        => SameRecipient(left, DadMemberCompletionRecipient.From(right));

    public static bool SameIntent(DadMemberCompletionCommand left, DadMemberCompletionCommand right)
        => left.RoundId == right.RoundId && left.RunId == right.RunId && left.Command == right.Command &&
           left.Recipient.WorkerSessionId == right.Recipient.WorkerSessionId &&
           left.Recipient.AssignedSlotId == right.Recipient.AssignedSlotId && SameRecipient(left.Recipient, right.Recipient);
}

public sealed class DadPostRunUtilities
{
    public bool OpenGearCoffers { get; set; }
    public bool RegisterTripleTriadCards { get; set; }
    public bool SellTripleTriadCards { get; set; }
    public bool GrandCompanyHandInViaAutoRetainer { get; set; }
    public string GrandCompanyHandInCommand { get; set; } = "/ays gc";

    public DadPostRunUtilities Clone()
        => new()
        {
            OpenGearCoffers = OpenGearCoffers,
            RegisterTripleTriadCards = RegisterTripleTriadCards,
            SellTripleTriadCards = SellTripleTriadCards,
            GrandCompanyHandInViaAutoRetainer = GrandCompanyHandInViaAutoRetainer,
            GrandCompanyHandInCommand = GrandCompanyHandInCommand,
        };
}
