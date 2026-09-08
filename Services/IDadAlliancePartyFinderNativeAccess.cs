using dad.Models;

namespace dad.Services;

internal interface IDadAlliancePartyFinderEditor : IDadAlliancePartyFinderCreateUi, IDadAlliancePartyFinderCleanupUi, IDisposable
{
    void ResetErrors();
    void StopCreate();
}

internal sealed record DadAlliancePfEnvironment(
    bool AgentAvailable, bool Recruiting, bool EditorVisible, int Passcode,
    int NumberOfGroups, string DutyName, bool ExistingParty, DadAllianceAssignment Alliance);

// Raw game/UI boundary; the gateway retains safety, hydration, flows and callback encoding.
internal interface IDadAlliancePartyFinderNativeAccess : IDadAlliancePartyFinderEditor
{
    DadAlliancePfEnvironment ReadEnvironment(ulong contentId);
    DadAlliancePfJoinSnapshot ReadJoin(DadAlliancePfJoinTarget target);
    IDadAlliancePfNativeCallbackSink CallbackSink { get; }
    DadAlliancePfJoinActionResult Show();
    nint ResolveAddon(string name, DadAlliancePfJoinAction action);
    (bool Visible, bool Ready, string Identity, string Text) ReadLeavePrompt();
    bool RequestLeave(out string error);
    bool ConfirmLeave();
}
