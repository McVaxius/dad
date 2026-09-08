using dad.Models;
using dad.Services;

namespace dad.Headless;

internal sealed class VirtualPartyGateway(ulong localContentId, Action<string> record) : IDadPartyNativeAccess
{
    private int promptSequence;
    public List<DadPartyMemberSnapshot> Members { get; set; } = [new() { ContentId = localContentId, IsLocalPlayer = true }];
    public bool CrossRealm { get; set; }
    public DadSelectYesnoPromptSnapshot Prompt { get; set; }
    public DadPendingPartyInvitation Invitation { get; set; }
    public DadPartyNativeObservation Observe() => new(Members, Members.FirstOrDefault()?.ContentId ?? 0,
        CrossRealm, Members.Count > 1, Prompt, Invitation, CurrentWorldId: 1);
    public bool InviteSameWorld(ulong contentId, string name, ushort worldId)
    { record($"native:invite:same-world:{contentId}:{worldId}"); return true; }
    public bool InviteCrossWorld(ulong contentId, ushort worldId)
    { record($"native:invite:cross-world:{contentId}:{worldId}"); return true; }
    public bool InviteInInstance(ulong contentId)
    { record($"native:invite:instance:{contentId}"); return true; }
    public bool RespondToInvitation(DadPendingPartyInvitation invitation)
    { record("native:accept-invitation"); return true; }
    public bool RestoreInvitationPrompt() { record("native:restore-invitation-prompt"); return true; }
    public bool ApprovePrompt()
    {
        if (!Prompt.Visible || !Prompt.Ready || Prompt.Text is not ("Disband the party?" or "Leave the party?"))
            throw new InvalidOperationException("Unexpected party prompt approval.");
        record("native:approve-party-prompt");
        Members = Members.Where(m => m.ContentId == localContentId).ToList();
        CrossRealm = false;
        Prompt = default;
        return true;
    }
    public bool LeaveThroughPartyMenu() { record("native:party-menu-leave"); Members = Members.Where(m => m.ContentId == localContentId).ToList(); return true; }
    public void SendChat(string command)
    {
        if (command is not ("/partycmd breakup" or "/partycmd leave")) throw new InvalidOperationException($"Unknown native party command: {command}");
        record($"native:party-command:{command}");
        Prompt = new(true, true, $"synthetic-party-prompt-{++promptSequence}",
            command == "/partycmd breakup" ? "Disband the party?" : "Leave the party?");
    }
}
