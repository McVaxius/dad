using dad.Models;

namespace dad.Services;

internal sealed record DadPartyNativeObservation(
    IReadOnlyList<DadPartyMemberSnapshot> Members,
    ulong LeaderContentId,
    bool CrossRealm,
    bool PartyMenuVisible,
    DadSelectYesnoPromptSnapshot Prompt,
    DadPendingPartyInvitation Invitation,
    bool OtherReadyPromptVisible = false,
    uint CurrentWorldId = 0);

// Game observations and native callbacks only; invitation ownership, retries,
// departure and teardown controllers remain in the production services.
internal interface IDadPartyNativeAccess : IDadNativePartyInviteDispatcher
{
    DadPartyNativeObservation Observe();
    bool RespondToInvitation(DadPendingPartyInvitation invitation);
    bool RestoreInvitationPrompt();
    bool ApprovePrompt();
    bool LeaveThroughPartyMenu();
    void SendChat(string command);
}
