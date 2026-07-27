using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

/// <summary>
/// DAD-owned source adaptation of PartyFinderPresets' LoadPreset/RCRefresh
/// transaction as observed at Krepyn/PartyFinderPresets commit 76610bac.
/// PartyFinderPresets is not discovered, loaded, reflected into, or called at runtime.
/// </summary>
internal sealed unsafe class DadAlliancePartyFinderPresetLoader :
    IDadAlliancePartyFinderPresetLoader
{
    internal const string RefreshSignature =
        "E8 ?? ?? ?? ?? 4D 89 A7 ?? ?? ?? ?? 4D 89 A7";

    private delegate void RecruitmentConditionRefreshDelegate(
        AgentLookingForGroup* agent,
        ulong recruitmentStatus,
        byte unknown);

    [Signature(RefreshSignature)]
    private RecruitmentConditionRefreshDelegate?
        refreshRecruitmentCondition = null;

    private readonly string initializationError = string.Empty;
    private bool disposed;

    public DadAlliancePartyFinderPresetLoader(
        IGameInteropProvider gameInteropProvider)
    {
        ArgumentNullException.ThrowIfNull(gameInteropProvider);
        try
        {
            gameInteropProvider.InitializeFromAttributes(this);
        }
        catch (Exception exception)
        {
            initializationError =
                $"The DAD-owned Party Finder refresh signature failed to initialize: {exception.Message}";
        }
    }

    public bool IsAvailable
        => !disposed &&
           refreshRecruitmentCondition != null &&
           sizeof(AgentLookingForGroup) ==
               DadAlliancePartyFinderApi15Layout.AgentLookingForGroupSize &&
           sizeof(AgentLookingForGroup.RecruitmentSub) ==
               DadAlliancePartyFinderApi15Layout.RecruitmentSubSize;

    public string UnavailableReason
        => IsAvailable
            ? string.Empty
            : disposed
                ? "The DAD-owned Party Finder preset loader is disposed."
                : sizeof(AgentLookingForGroup) !=
                  DadAlliancePartyFinderApi15Layout.AgentLookingForGroupSize ||
                  sizeof(AgentLookingForGroup.RecruitmentSub) !=
                  DadAlliancePartyFinderApi15Layout.RecruitmentSubSize
                    ? "The installed API-15 Party Finder struct layout does not match DAD's pinned contract."
            : string.IsNullOrWhiteSpace(initializationError)
                ? "The DAD-owned Party Finder refresh signature is unavailable."
                : initializationError;

    public DadAlliancePfCreateActionResult Apply(int passcode)
    {
        if (!IsAvailable)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The DAD-owned Alliance preset loader is unavailable.",
                UnavailableReason);
        }
        if (passcode is < 1000 or > 9999)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The DAD-owned Alliance preset was not loaded.",
                "The Party Finder passcode must be exactly four digits.");
        }

        var condition = GetAddon<AddonLookingForGroupCondition>(
            "LookingForGroupCondition");
        if (condition == null ||
            !condition->AtkUnitBase.IsVisible ||
            !condition->AtkUnitBase.IsReady)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The DAD-owned Alliance preset was not loaded.",
                "Party Finder recruitment conditions are not ready.");
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The DAD-owned Alliance preset was not loaded.",
                "Party Finder agent is unavailable.");
        }

        DadAlliancePartyFinderApi15PresetState original;
        DadAlliancePartyFinderApi15PresetState preset;
        try
        {
            original = DadAlliancePartyFinderPresetRules.Capture(
                new ReadOnlySpan<byte>(
                    &agent->StoredRecruitmentInfo,
                    DadAlliancePartyFinderApi15Layout.RecruitmentSubSize),
                agent->GroupTypeTab,
                agent->AvgItemLvEnabled,
                agent->AvgItemLv);
            preset = DadAlliancePartyFinderPresetRules.Build(
                original,
                passcode);
        }
        catch (Exception exception)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The DAD-owned Alliance preset was not loaded.",
                exception.Message);
        }

        var agentAddress = (nint)agent;
        var transaction = DadAlliancePartyFinderPresetTransaction.Execute(
            apply: () =>
            {
                var currentAgent =
                    (AgentLookingForGroup*)agentAddress;
                WriteState(currentAgent, preset);
            },
            refresh: () =>
            {
                var currentAgent =
                    (AgentLookingForGroup*)agentAddress;
                refreshRecruitmentCondition!(
                    currentAgent,
                    0,
                    0);
            },
            rollback: () =>
            {
                var currentAgent =
                    (AgentLookingForGroup*)agentAddress;
                WriteState(currentAgent, original);
            });

        return transaction.Success
            ? new DadAlliancePfCreateActionResult(
                true,
                "Captured the game-owned API-15 selector, loaded one full Alliance A preset, and invoked one recruitment-editor refresh.")
            : new DadAlliancePfCreateActionResult(
                false,
                "The DAD-owned Alliance preset refresh failed and its complete API-15 agent snapshot was restored.",
                transaction.Error);
    }

    public void Dispose()
    {
        disposed = true;
        refreshRecruitmentCondition = null;
    }

    private static void WriteState(
        AgentLookingForGroup* agent,
        DadAlliancePartyFinderApi15PresetState state)
    {
        state.RecruitmentSub.AsSpan().CopyTo(
            new Span<byte>(
                &agent->StoredRecruitmentInfo,
                DadAlliancePartyFinderApi15Layout.RecruitmentSubSize));
        agent->GroupTypeTab = state.GroupTypeTab;
        agent->AvgItemLvEnabled = state.AvgItemLvEnabled;
        agent->AvgItemLv = state.AvgItemLv;
    }

    private static T* GetAddon<T>(string name) where T : unmanaged
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null ? null : (T*)manager->GetAddonByName(name);
    }
}
