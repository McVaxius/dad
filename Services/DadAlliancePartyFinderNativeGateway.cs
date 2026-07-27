using dad.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace dad.Services;

internal enum DadAllianceNativeStepKind
{
    Progress,
    Waiting,
    Retry,
    Succeeded,
    Stopped,
    Blocked,
}

internal readonly record struct DadAllianceNativeStep(
    DadAllianceNativeStepKind Kind,
    DadAllianceRecruitmentState State,
    string Summary,
    ulong ListingId = 0,
    DadAllianceAssignment ObservedAlliance = DadAllianceAssignment.None,
    string CreateStage = "",
    string CreateEvent = "",
    int Attempt = 0,
    DateTime? NextRetryUtc = null,
    string LastError = "",
    string Readiness = "",
    uint Category = 0,
    ushort DutyId = 0,
    int ElapsedMilliseconds = 0,
    bool ActiveRecruitment = false,
    bool EditorVisible = false,
    bool SubmitDispatched = false,
    string ConfigurationTarget = "",
    string ObservedSettings = "",
    bool ShouldAudit = false);

/// <summary>
/// Framework-thread-only API-15 Party Finder gateway. It uses generated
/// ClientStructs surfaces and addon components plus DAD's fail-closed,
/// self-contained recruitment-editor refresh adapter.
/// </summary>
internal sealed unsafe class DadAlliancePartyFinderNativeGateway : IDisposable
{
    public const string FormationDutyName = "The Labyrinth of the Ancients";
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IPartyList partyList;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly DadAlliancePartyFinderECommonsAdapter createUi;

    private DadAlliancePartyFinderCreateFlow createFlow;
    private DadAlliancePartyFinderCleanupFlow cleanupFlow;
    private ulong pendingListingId;
    private int listingCursor;
    private DateTime nextListingRefreshUtc = DateTime.MinValue;
    private string activeJoinKey = string.Empty;
    private string leavePromptBaseline = string.Empty;
    private bool leaveRequested;

    public DadAlliancePartyFinderNativeGateway(
        IFramework framework,
        ICondition condition,
        IPartyList partyList,
        IObjectTable objectTable,
        DadPresenceService presenceService,
        IDadGameCommandExecutor gameCommandExecutor,
        IDataManager dataManager,
        IToastGui toastGui,
        IGameInteropProvider gameInteropProvider,
        IPluginLog log)
    {
        this.framework = framework;
        this.condition = condition;
        this.partyList = partyList;
        this.presenceService = presenceService;
        this.dataManager = dataManager;
        this.log = log;
        var nativeActions = new DadAlliancePartyFinderTypedNativeActions();
        var recruitmentObserver = new DadAllianceLocalRecruitmentObserver(objectTable);
        var presetLoader =
            new DadAlliancePartyFinderPresetLoader(gameInteropProvider);
        createUi = new DadAlliancePartyFinderECommonsAdapter(
            gameCommandExecutor,
            nativeActions,
            presetLoader,
            recruitmentObserver,
            dataManager,
            toastGui);
        createFlow = new DadAlliancePartyFinderCreateFlow(createUi);
        cleanupFlow = new DadAlliancePartyFinderCleanupFlow(createUi);
    }

    public DadParticipantSnapshot BuildLocalSnapshot()
        => presenceService.BuildLiveSafetySnapshot();

    public DadAllianceNativeStep AdvanceCreate(int passcode)
    {
        RequireFrameworkThread();
        var safety = ValidateSafeMutation(requireSolo: true);
        if (!string.IsNullOrWhiteSpace(safety))
        {
            return new DadAllianceNativeStep(
                DadAllianceNativeStepKind.Waiting,
                DadAllianceRecruitmentState.WaitingUnsafe,
                safety,
                CreateStage: createFlow.Stage.ToString(),
                Attempt: createFlow.Attempt,
                NextRetryUtc: createFlow.NextRetryUtc,
                LastError: createFlow.LastError,
                Readiness: "unsafe",
                ConfigurationTarget: string.Empty,
                ShouldAudit: true);
        }

        var result = createFlow.Advance(passcode);
        return new DadAllianceNativeStep(
            result.Kind switch
            {
                DadAlliancePfCreateResultKind.Progress => DadAllianceNativeStepKind.Progress,
                DadAlliancePfCreateResultKind.Waiting => DadAllianceNativeStepKind.Waiting,
                DadAlliancePfCreateResultKind.Retry => DadAllianceNativeStepKind.Retry,
                DadAlliancePfCreateResultKind.Succeeded => DadAllianceNativeStepKind.Succeeded,
                DadAlliancePfCreateResultKind.Stopped => DadAllianceNativeStepKind.Stopped,
                DadAlliancePfCreateResultKind.Blocked => DadAllianceNativeStepKind.Blocked,
                _ => DadAllianceNativeStepKind.Blocked,
            },
            result.Kind == DadAlliancePfCreateResultKind.Succeeded
                ? DadAllianceRecruitmentState.ListingOpen
                : result.Kind == DadAlliancePfCreateResultKind.Stopped
                    ? DadAllianceRecruitmentState.Stopped
                    : result.Kind == DadAlliancePfCreateResultKind.Blocked
                        ? DadAllianceRecruitmentState.Blocked
                        : result.Kind == DadAlliancePfCreateResultKind.Retry ||
                          (result.Kind == DadAlliancePfCreateResultKind.Waiting &&
                           string.Equals(result.Event, "retry-wait", StringComparison.Ordinal))
                            ? DadAllianceRecruitmentState.RetryWaiting
                            : DadAllianceRecruitmentState.CreatingListing,
            result.Summary,
            result.ListingId,
            CreateStage: result.Stage.ToString(),
            CreateEvent: result.Event,
            Attempt: result.Attempt,
            NextRetryUtc: result.NextRetryUtc,
            LastError: result.LastError,
            Readiness: result.Readiness,
            Category: result.Category,
            DutyId: result.DutyId,
            ElapsedMilliseconds: result.ElapsedMilliseconds,
            ActiveRecruitment: result.ActiveRecruitment,
            EditorVisible: result.EditorVisible,
            SubmitDispatched: result.SubmitDispatched,
            ConfigurationTarget: result.ConfigurationTarget,
            ObservedSettings: result.ObservedSettings,
            ShouldAudit: result.ShouldAudit);
    }

    public DadAllianceNativeStep AdvanceJoin(DadAllianceRecruitmentInstructionDto instruction)
    {
        RequireFrameworkThread();
        var instructionBlocker = DadAlliancePartyFinderRules.ValidateInstruction(instruction);
        if (!string.IsNullOrWhiteSpace(instructionBlocker))
            return Blocked(instructionBlocker);

        var joinKey = instruction.DedupeKey;
        if (!string.Equals(activeJoinKey, joinKey, StringComparison.OrdinalIgnoreCase))
        {
            ResetJoinState();
            activeJoinKey = joinKey;
        }

        var local = presenceService.BuildLiveSafetySnapshot();
        if (!string.Equals(
                local.ActiveCharacterKey.Value,
                instruction.TargetCharacterKey.Value,
                StringComparison.OrdinalIgnoreCase) ||
            local.Character.ContentId != instruction.TargetContentId)
        {
            return Blocked("The active local character contradicts the exact alliance recruitment target.");
        }

        var observed = ObserveAlliance(instruction.TargetContentId);
        if (observed == instruction.AssignedAlliance)
        {
            return new DadAllianceNativeStep(
                DadAllianceNativeStepKind.Succeeded,
                DadAllianceRecruitmentState.Complete,
                $"Verified exact Alliance {observed}.",
                pendingListingId,
                observed);
        }

        var agent = AgentLookingForGroup.Instance();
        var isLocalCreator =
            agent != null &&
            agent->OwnListingId != 0 &&
            string.Equals(
                local.Character.CharacterName?.Trim(),
                instruction.LeaderName.Trim(),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                local.Character.WorldName?.Trim(),
                instruction.LeaderWorld.Trim(),
                StringComparison.OrdinalIgnoreCase);
        if (isLocalCreator)
        {
            var recruitment = agent->StoredRecruitmentInfo;
            if (instruction.AssignedAlliance != DadAllianceAssignment.A ||
                recruitment.Password != instruction.Passcode ||
                recruitment.NumberOfGroups != 3 ||
                !string.Equals(
                    ResolveDutyName(recruitment.SelectedDutyId),
                    FormationDutyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Blocked("The local PF creator contradicts the exact Alliance-A Labyrinth recruitment.");
            }

            return Waiting(
                DadAllianceRecruitmentState.Verifying,
                "The Alliance-A PF creator is waiting for cross-realm subgroup verification.",
                observed);
        }

        var safety = ValidateSafeMutation(requireSolo: false);
        if (!string.IsNullOrWhiteSpace(safety))
            return Waiting(DadAllianceRecruitmentState.WaitingUnsafe, safety, observed);

        if (observed != DadAllianceAssignment.None || IsInExistingParty())
        {
            var leave = AdvanceGuardedLeave();
            if (leave.Kind != DadAllianceNativeStepKind.Succeeded)
                return leave with { ObservedAlliance = observed };
            ResetSearchState();
        }

        if (agent == null)
            return Retry(DadAllianceRecruitmentState.Searching, "Party Finder agent is unavailable.", observed);

        if (pendingListingId != 0)
        {
            if (agent->LastViewedListing.ListingId != pendingListingId)
                return Waiting(DadAllianceRecruitmentState.Searching, "Waiting for Party Finder listing details.", observed);

            var detail = agent->LastViewedListing;
            var world = ResolveWorldName(detail.HomeWorld);
            if (DadAlliancePartyFinderRules.IsExactListingMatch(
                    detail.LeaderString,
                    world,
                    instruction.LeaderName,
                    instruction.LeaderWorld) &&
                detail.IsAlliance &&
                detail.NumberOfParties == 3)
            {
                agent->StoredRecruitmentInfo.Password = checked((ushort)instruction.Passcode);
                var addon = GetAddon<AddonLookingForGroupDetail>("LookingForGroupDetail");
                if (addon == null || !addon->AtkUnitBase.IsVisible)
                    return Waiting(DadAllianceRecruitmentState.Joining, "Waiting for exact listing details.", observed);

                var buttonIndex = DadAlliancePartyFinderRules.GetJoinAllianceButtonIndex(instruction.AssignedAlliance);
                var buttons = addon->JoinAllianceButtons;
                if (buttonIndex < 0 ||
                    buttonIndex >= buttons.Length ||
                    !ClickButton(buttons[buttonIndex].Value, &addon->AtkUnitBase))
                    return Retry(DadAllianceRecruitmentState.Joining, $"Alliance {instruction.AssignedAlliance} join button is unavailable.", observed);

                return Progress(
                    $"Submitted the four-digit password and Alliance {instruction.AssignedAlliance} join button.",
                    DadAllianceRecruitmentState.Joining,
                    observed);
            }

            pendingListingId = 0;
        }

        var now = DateTime.UtcNow;
        if (now >= nextListingRefreshUtc)
        {
            agent->Show();
            agent->RequestCategoryListings((byte)AgentLookingForGroup.DutyCategory.Raids);
            agent->RequestListingsUpdate();
            listingCursor = 0;
            nextListingRefreshUtc = now + TimeSpan.FromSeconds(2);
        }

        var listingIds = agent->Listings.ListingIds;
        while (listingCursor < listingIds.Length)
        {
            var listingId = listingIds[listingCursor++];
            if (listingId == 0)
                continue;
            if (!agent->OpenListing(listingId))
                continue;
            pendingListingId = listingId;
            return Progress("Inspecting a Party Finder listing by exact leader and home world.", DadAllianceRecruitmentState.Searching, observed);
        }

        return Retry(
            DadAllianceRecruitmentState.Searching,
            $"No exact listing for {instruction.LeaderName} on {instruction.LeaderWorld} is currently visible.",
            observed);
    }

    public DadAllianceNativeStep AdvanceEndRecruitment(ulong expectedOwnerHandle)
    {
        RequireFrameworkThread();
        var safety = ValidateSafeMutation(requireSolo: false, allowParty: true);
        if (!string.IsNullOrWhiteSpace(safety))
            return Waiting(DadAllianceRecruitmentState.WaitingUnsafe, safety);

        var result = cleanupFlow.Advance(expectedOwnerHandle);
        return new DadAllianceNativeStep(
            result.Kind switch
            {
                DadAlliancePfCreateResultKind.Progress => DadAllianceNativeStepKind.Progress,
                DadAlliancePfCreateResultKind.Waiting => DadAllianceNativeStepKind.Waiting,
                DadAlliancePfCreateResultKind.Retry => DadAllianceNativeStepKind.Retry,
                DadAlliancePfCreateResultKind.Succeeded => DadAllianceNativeStepKind.Succeeded,
                DadAlliancePfCreateResultKind.Stopped => DadAllianceNativeStepKind.Stopped,
                DadAlliancePfCreateResultKind.Blocked => DadAllianceNativeStepKind.Blocked,
                _ => DadAllianceNativeStepKind.Blocked,
            },
            result.Kind == DadAlliancePfCreateResultKind.Succeeded
                ? DadAllianceRecruitmentState.Complete
                : result.Kind == DadAlliancePfCreateResultKind.Blocked
                    ? DadAllianceRecruitmentState.Blocked
                    : result.Kind == DadAlliancePfCreateResultKind.Retry
                        ? DadAllianceRecruitmentState.RetryWaiting
                        : DadAllianceRecruitmentState.ListingOpen,
            result.Summary,
            result.OwnerHandle,
            CreateStage: $"Cleanup:{result.Stage}",
            CreateEvent: result.Event,
            Attempt: result.Attempt,
            NextRetryUtc: result.NextRetryUtc,
            LastError: result.LastError,
            Readiness: result.Readiness,
            ActiveRecruitment: result.ActiveRecruitment,
            SubmitDispatched: true,
            ShouldAudit: result.ShouldAudit);
    }

    public DadAllianceAssignment ObserveAlliance(ulong contentId)
    {
        RequireFrameworkThread();
        if (contentId == 0 || !InfoProxyCrossRealm.IsAllianceRaid())
            return DadAllianceAssignment.None;

        var member = InfoProxyCrossRealm.GetMemberByContentId(contentId);
        return member == null
            ? DadAllianceAssignment.None
            : DadAlliancePartyFinderRules.FromCrossRealmGroupIndex(member->GroupIndex);
    }

    public void Reset()
    {
        RequireFrameworkThread();
        createUi.ResetErrors();
        createFlow = new DadAlliancePartyFinderCreateFlow(createUi);
        cleanupFlow = new DadAlliancePartyFinderCleanupFlow(createUi);
        ResetJoinState();
    }

    public void StopCreate()
    {
        RequireFrameworkThread();
        createFlow.Stop();
        createUi.StopCreate();
    }

    public void Dispose()
        => createUi.Dispose();

    private DadAllianceNativeStep AdvanceGuardedLeave()
    {
        var prompt = ReadYesNoPrompt();
        if (!leaveRequested)
        {
            leavePromptBaseline = prompt.Identity;
            if (!TrySubmitGuardedLeaveCommand(out var leaveError))
                return Retry(DadAllianceRecruitmentState.CorrectingWrongAlliance, leaveError);
            leaveRequested = true;
            return Progress("Requested guarded departure before exact subgroup rejoin.", DadAllianceRecruitmentState.CorrectingWrongAlliance);
        }

        if (!IsInExistingParty())
        {
            leaveRequested = false;
            leavePromptBaseline = string.Empty;
            return new DadAllianceNativeStep(
                DadAllianceNativeStepKind.Succeeded,
                DadAllianceRecruitmentState.Searching,
                "Existing party or wrong alliance subgroup was left safely.");
        }

        if (!prompt.Visible)
            return Waiting(DadAllianceRecruitmentState.CorrectingWrongAlliance, "Waiting for the guarded leave confirmation.");
        if (string.Equals(prompt.Identity, leavePromptBaseline, StringComparison.Ordinal) ||
            !ContainsLeaveLanguage(prompt.Text))
        {
            return Blocked("A fresh party/alliance leave confirmation could not be proven; DAD will not click it.");
        }

        FireYes(prompt.Addon);
        return Progress("Confirmed guarded departure.", DadAllianceRecruitmentState.CorrectingWrongAlliance);
    }

    private static bool TrySubmitGuardedLeaveCommand(out string error)
    {
        const string leaveCommand = "/leave";
        error = string.Empty;
        Utf8String* entry = null;
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                error = "The native game UI module is unavailable for guarded /leave.";
                return false;
            }

            entry = Utf8String.FromString(leaveCommand);
            if (entry == null)
            {
                error = "The guarded /leave chat entry could not be allocated.";
                return false;
            }

            uiModule->ProcessChatBoxEntry(entry, nint.Zero);
            return true;
        }
        catch (Exception exception)
        {
            error = $"The guarded /leave command failed: {exception.Message}";
            return false;
        }
        finally
        {
            if (entry != null)
                entry->Dtor(true);
        }
    }

    private bool IsInExistingParty()
        => partyList.Length > 1 ||
           InfoProxyCrossRealm.IsCrossRealmParty() ||
           InfoProxyCrossRealm.IsLocalPlayerInParty();

    private string ValidateSafeMutation(bool requireSolo, bool allowParty = false)
    {
        var snapshot = presenceService.BuildLiveSafetySnapshot();
        if (!snapshot.WorldReadyStable || snapshot.Character.ContentId == 0)
            return "Waiting for a stable, world-ready local character.";
        if (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56])
            return "Waiting because the character is bound by a duty.";
        if (condition[ConditionFlag.InDutyQueue] ||
            condition[ConditionFlag.WaitingForDuty] ||
            condition[ConditionFlag.WaitingForDutyFinder])
        {
            return "Waiting because Duty Finder activity is already in progress.";
        }
        if (condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.Occupied] ||
            condition[ConditionFlag.Occupied30] ||
            condition[ConditionFlag.Occupied33] ||
            condition[ConditionFlag.Occupied38] ||
            condition[ConditionFlag.Occupied39] ||
            condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.OccupiedInEvent] ||
            condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.InCombat] ||
            condition[ConditionFlag.Casting] ||
            condition[ConditionFlag.TradeOpen])
        {
            return "Waiting for unsafe world/UI activity to end.";
        }
        if (requireSolo && !allowParty && IsInExistingParty())
            return "The Party Finder creator must be solo.";
        return string.Empty;
    }

    private string ResolveDutyName(ushort dutyId)
    {
        var sheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        return dutyId != 0 && sheet.TryGetRow(dutyId, out var duty)
            ? duty.Name.ToString().Trim()
            : string.Empty;
    }

    private string ResolveWorldName(ushort worldId)
    {
        var sheet = dataManager.GetExcelSheet<World>();
        return worldId != 0 && sheet.TryGetRow(worldId, out var world)
            ? world.Name.ToString().Trim()
            : string.Empty;
    }

    private void ResetJoinState()
    {
        activeJoinKey = string.Empty;
        ResetSearchState();
        leavePromptBaseline = string.Empty;
        leaveRequested = false;
    }

    private void ResetSearchState()
    {
        pendingListingId = 0;
        listingCursor = 0;
        nextListingRefreshUtc = DateTime.MinValue;
    }

    private static bool ClickButton(AtkComponentButton* button, AtkUnitBase* addon)
    {
        if (button == null ||
            addon == null ||
            !button->IsEnabled ||
            button->AtkResNode == null ||
            !button->AtkResNode->IsVisible())
        {
            return false;
        }
        (*button).ClickAddonButton(addon);
        return true;
    }

    private static T* GetAddon<T>(string name) where T : unmanaged
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null ? null : (T*)manager->GetAddonByName(name);
    }

    private static PromptSnapshot ReadYesNoPrompt()
    {
        var addon = GetAddon<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return default;
        var text = addon->PromptText == null
            ? string.Empty
            : addon->PromptText->NodeText.ToString().Trim();
        return new PromptSnapshot(
            true,
            $"{(nint)addon:X}:{text}",
            text,
            &addon->AtkUnitBase);
    }

    private static void FireYes(AtkUnitBase* addon)
    {
        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        addon->FireCallback(1, values, true);
    }

    private static bool ContainsLeaveLanguage(string text)
        => text.Contains("leave", StringComparison.OrdinalIgnoreCase) &&
           (text.Contains("party", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("alliance", StringComparison.OrdinalIgnoreCase));

    private DadAllianceNativeStep Progress(
        string summary,
        DadAllianceRecruitmentState state = DadAllianceRecruitmentState.CreatingListing,
        DadAllianceAssignment observed = DadAllianceAssignment.None)
        => new(DadAllianceNativeStepKind.Progress, state, summary, pendingListingId, observed);

    private static DadAllianceNativeStep Waiting(
        DadAllianceRecruitmentState state,
        string summary,
        DadAllianceAssignment observed = DadAllianceAssignment.None)
        => new(DadAllianceNativeStepKind.Waiting, state, summary, 0, observed);

    private static DadAllianceNativeStep Retry(
        DadAllianceRecruitmentState state,
        string summary,
        DadAllianceAssignment observed = DadAllianceAssignment.None)
        => new(DadAllianceNativeStepKind.Retry, state, summary, 0, observed);

    private static DadAllianceNativeStep Blocked(string summary)
        => new(
            DadAllianceNativeStepKind.Blocked,
            DadAllianceRecruitmentState.Blocked,
            summary);

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Alliance Party Finder native work must run on the framework thread.");
    }

    private readonly struct PromptSnapshot
    {
        public PromptSnapshot(
            bool visible,
            string identity,
            string text,
            AtkUnitBase* addon)
        {
            Visible = visible;
            Identity = identity;
            Text = text;
            Addon = addon;
        }

        public bool Visible { get; }
        public string Identity { get; }
        public string Text { get; }
        public AtkUnitBase* Addon { get; }
    }
}
