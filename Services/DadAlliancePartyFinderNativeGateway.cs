using System.Text;
using dad.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
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
    bool ParticipatingInCrossWorldPartyOrAlliance = false,
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
internal sealed unsafe class DadAlliancePartyFinderNativeGateway :
    IDisposable,
    IDadAlliancePartyFinderJoinUi
{
    public const string FormationDutyName = "The Labyrinth of the Ancients";
    private const int MaxListingRendererScan = 100;
    private const int MaxListingTreeNodes = 256;
    private const int MaxListingTreeDepth = 8;
    private const int MaxDiagnosticNodes = 16_384;
    private const int MaxDiagnosticTreeDepth = 64;
    private const int MaxDiagnosticRendererSlots = 100;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IPartyList partyList;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly DadAlliancePartyFinderECommonsAdapter createUi;
    private readonly DadAlliancePartyFinderNativeCallbackDispatcher
        joinCallbackDispatcher;

    private DadAlliancePartyFinderCreateFlow createFlow;
    private DadAlliancePartyFinderCleanupFlow cleanupFlow;
    private DadAlliancePartyFinderJoinFlow joinFlow;
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
        joinCallbackDispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(
                LogJoinCallbackTrace);
        var nativeActions = new DadAlliancePartyFinderTypedNativeActions();
        var recruitmentObserver =
            new DadAllianceLocalRecruitmentObserver(condition);
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
        joinFlow = new DadAlliancePartyFinderJoinFlow(this);
    }

    public DadParticipantSnapshot BuildLocalSnapshot()
        => presenceService.BuildLiveSafetySnapshot();

    public Task<string> CaptureLookingForGroupDiagnosticsAsync(
        DateTime capturedAtUtc)
        => framework.RunOnFrameworkThread(
            () => CaptureLookingForGroupDiagnosticsOnFrameworkThread(
                capturedAtUtc));

    private string CaptureLookingForGroupDiagnosticsOnFrameworkThread(
        DateTime capturedAtUtc)
    {
        RequireFrameworkThread();
        capturedAtUtc = capturedAtUtc.Kind == DateTimeKind.Utc
            ? capturedAtUtc
            : capturedAtUtc.ToUniversalTime();
        var context = new LookingForGroupDiagnosticCaptureContext();
        var agent = AgentLookingForGroup.Instance();
        var addon = GetAddon<AddonLookingForGroup>("LookingForGroup");
        var addonBase = addon == null
            ? null
            : &addon->AddonLookingForGroupBase.AtkUnitBase;
        var displayedListings =
            agent == null ? 0 : agent->NumberOfListingsDisplayed;

        context.Builder.AppendLine("DAD LookingForGroup read-only diagnostic");
        context.Builder.AppendLine($"CapturedUtc={capturedAtUtc:O}");
        context.Builder.AppendLine(
            "MutationPolicy=no refresh, navigation, click, callback, agent show/open, or UI state change");
        context.Builder.AppendLine(
            $"Limits=nodes:{MaxDiagnosticNodes}; depth:{MaxDiagnosticTreeDepth}; renderer-slots:{MaxDiagnosticRendererSlots}");
        context.Builder.AppendLine();
        context.Builder.AppendLine("[agent]");
        context.Builder.AppendLine(
            $"address={FormatDiagnosticAddress((nint)agent)} available={agent != null}");
        if (agent != null)
        {
            context.Builder.AppendLine(
                $"tabs search-area={agent->SearchAreaTab} category={agent->CategoryTab} group-type={agent->GroupTypeTab}");
            context.Builder.AppendLine(
                $"number-of-listings-displayed={displayedListings}");
        }
        else
        {
            context.Builder.AppendLine(
                "tabs=<unavailable>; number-of-listings-displayed=<unavailable>");
        }

        context.Builder.AppendLine();
        context.Builder.AppendLine("[addon LookingForGroup]");
        context.Builder.AppendLine(
            $"address={FormatDiagnosticAddress((nint)addon)} atk-unit-base={FormatDiagnosticAddress((nint)addonBase)} available={addon != null}");
        context.Builder.AppendLine(
            addonBase == null
                ? "visible=<unavailable> ready=<unavailable>"
                : $"visible={addonBase->IsVisible} ready={addonBase->IsReady}");

        try
        {
            DumpDiagnosticList(
                "standard",
                addon == null ? null : addon->StandardViewList,
                displayedListings,
                context);
        }
        catch (Exception exception)
        {
            context.Builder.AppendLine(
                $"[list standard] <capture-error: {EscapeDiagnosticText(exception.Message)}>");
        }

        try
        {
            DumpDiagnosticList(
                "compact",
                addon == null ? null : addon->CompactViewList,
                displayedListings,
                context);
        }
        catch (Exception exception)
        {
            context.Builder.AppendLine(
                $"[list compact] <capture-error: {EscapeDiagnosticText(exception.Message)}>");
        }

        context.Builder.AppendLine();
        context.Builder.AppendLine("[addon ULD tree]");
        try
        {
            if (addonBase == null)
            {
                context.Builder.AppendLine(
                    "path=addon/uld manager=<null>");
            }
            else
            {
                DumpDiagnosticUldManager(
                    &addonBase->UldManager,
                    "addon/uld",
                    0,
                    context);
            }
        }
        catch (Exception exception)
        {
            context.Builder.AppendLine(
                $"path=addon/uld <capture-error: {EscapeDiagnosticText(exception.Message)}>");
        }

        context.Builder.AppendLine();
        context.Builder.AppendLine("[capture summary]");
        context.Builder.AppendLine(
            $"renderer-slots-inspected={context.RendererSlots}; node-entries-inspected={context.NodeEntries}; " +
            $"unique-managers={context.SeenManagers.Count}; unique-nodes={context.SeenNodes.Count}; " +
            $"renderer-truncated={context.RendererTruncated}; node-truncated={context.NodeTruncated}; " +
            $"depth-truncated={context.DepthTruncated}");
        return context.Builder.ToString();
    }

    private static void DumpDiagnosticList(
        string name,
        AtkComponentList* list,
        int displayedListings,
        LookingForGroupDiagnosticCaptureContext context)
    {
        context.Builder.AppendLine();
        context.Builder.AppendLine($"[list {name}]");
        if (list == null)
        {
            context.Builder.AppendLine("address=<null> available=false");
            return;
        }

        var root = list->AtkResNode;
        context.Builder.AppendLine(
            $"address={FormatDiagnosticAddress((nint)list)} root={FormatDiagnosticAddress((nint)root)} " +
            $"visible={ReadDiagnosticVisibility(root)} loaded-state={list->UldManager.LoadedState}");
        context.Builder.AppendLine(
            $"list-length={list->ListLength}; allocated-renderer-slots={list->AllocatedItemRendererListLength}; " +
            $"renderer-storage={FormatDiagnosticAddress((nint)list->ItemRendererList)}; " +
            $"first-visible={list->FirstVisibleItemIndex}; visible-rows={list->VisibleRowCount}; " +
            $"selected={list->SelectedItemIndex}; hovered={list->HoveredItemIndex}");
        if (list->ListLength < 0)
        {
            context.Builder.AppendLine(
                $"<invalid list-length: {list->ListLength}>");
        }

        var rendererCount = list->AllocatedItemRendererListLength;
        if (rendererCount < 0)
        {
            context.Builder.AppendLine(
                $"<invalid allocated-renderer-slots: {rendererCount}>");
        }
        else if (rendererCount > 0 && list->ItemRendererList == null)
        {
            context.Builder.AppendLine(
                "<invalid renderer storage: nonzero slot count with null pointer>");
        }
        else
        {
            var inspectedHere = 0;
            for (var storageIndex = 0;
                 storageIndex < rendererCount &&
                 context.RendererSlots < MaxDiagnosticRendererSlots;
                 storageIndex++)
            {
                inspectedHere++;
                context.RendererSlots++;
                var renderer =
                    list->ItemRendererList[storageIndex]
                        .AtkComponentListItemRenderer;
                var path = $"lists/{name}/renderer-slot[{storageIndex}]";
                if (renderer == null)
                {
                    context.Builder.AppendLine(
                        $"path={path} storage-index={storageIndex} renderer=<null>");
                    continue;
                }

                var listItemIndex = renderer->ListItemIndex;
                var validListItemIndex =
                    listItemIndex >= 0 &&
                    listItemIndex < list->ListLength &&
                    listItemIndex < displayedListings;
                context.Builder.AppendLine(
                    $"path={path} storage-index={storageIndex} " +
                    $"renderer={FormatDiagnosticAddress((nint)renderer)} " +
                    $"ListItemIndex={listItemIndex} valid-index={validListItemIndex} " +
                    $"root={FormatDiagnosticAddress((nint)renderer->AtkResNode)} " +
                    $"component-flags=0x{renderer->ComponentFlags:X8}");
                if (!validListItemIndex)
                {
                    context.Builder.AppendLine(
                        $"path={path} <invalid ListItemIndex for list-length={list->ListLength} " +
                        $"and displayed-listings={displayedListings}>");
                }

                DumpDiagnosticUldManager(
                    &renderer->UldManager,
                    $"{path}/uld",
                    0,
                    context);
            }

            if (rendererCount > inspectedHere)
            {
                context.RendererTruncated = true;
                context.Builder.AppendLine(
                    $"<renderer slots truncated: inspected {inspectedHere} of {rendererCount}; " +
                    $"global limit {MaxDiagnosticRendererSlots}>");
            }
        }

        DumpDiagnosticUldManager(
            &list->UldManager,
            $"lists/{name}/component-uld",
            0,
            context);
    }

    private static void DumpDiagnosticUldManager(
        AtkUldManager* manager,
        string path,
        int depth,
        LookingForGroupDiagnosticCaptureContext context)
    {
        if (manager == null)
        {
            context.Builder.AppendLine($"path={path} manager=<null>");
            return;
        }
        if (depth > MaxDiagnosticTreeDepth)
        {
            context.DepthTruncated = true;
            context.Builder.AppendLine(
                $"path={path} manager={FormatDiagnosticAddress((nint)manager)} " +
                $"<depth truncated at {MaxDiagnosticTreeDepth}>");
            return;
        }

        var managerAddress = (nint)manager;
        if (!context.SeenManagers.Add(managerAddress))
        {
            context.Builder.AppendLine(
                $"path={path} manager={FormatDiagnosticAddress(managerAddress)} " +
                "<cycle/duplicate manager>");
            return;
        }

        context.Builder.AppendLine(
            $"path={path} manager={FormatDiagnosticAddress(managerAddress)} depth={depth} " +
            $"loaded-state={manager->LoadedState} base-type={manager->BaseType} " +
            $"resource-flags={manager->ResourceFlags} node-list-count={manager->NodeListCount} " +
            $"node-list-size={manager->NodeListSize} node-list={FormatDiagnosticAddress((nint)manager->NodeList)} " +
            $"root={FormatDiagnosticAddress((nint)manager->RootNode)} " +
            $"root-size={manager->RootNodeWidth}x{manager->RootNodeHeight}");
        if (manager->NodeListSize < manager->NodeListCount)
        {
            context.Builder.AppendLine(
                $"path={path} <invalid node-list-size {manager->NodeListSize} " +
                $"below count {manager->NodeListCount}>");
        }
        if (manager->NodeListCount > 0 && manager->NodeList == null)
        {
            context.Builder.AppendLine(
                $"path={path} <invalid node list: nonzero count with null pointer>");
            return;
        }

        for (var nodeListIndex = 0;
             nodeListIndex < manager->NodeListCount;
             nodeListIndex++)
        {
            if (context.NodeEntries >= MaxDiagnosticNodes)
            {
                context.NodeTruncated = true;
                context.Builder.AppendLine(
                    $"path={path} <nodes truncated at global limit {MaxDiagnosticNodes}; " +
                    $"next-node-list-index={nodeListIndex}>");
                return;
            }

            context.NodeEntries++;
            var node = manager->NodeList[nodeListIndex];
            var nodePath = $"{path}/node[{nodeListIndex}]";
            if (node == null)
            {
                context.Builder.AppendLine(
                    $"path={nodePath} node-list-index={nodeListIndex} address=<null>");
                continue;
            }

            DumpDiagnosticNode(
                node,
                nodePath,
                nodeListIndex,
                depth,
                context);
        }
    }

    private static void DumpDiagnosticNode(
        AtkResNode* node,
        string path,
        int nodeListIndex,
        int depth,
        LookingForGroupDiagnosticCaptureContext context)
    {
        var nodeAddress = (nint)node;
        if (!context.SeenNodes.Add(nodeAddress))
        {
            context.Builder.AppendLine(
                $"path={path} node-list-index={nodeListIndex} " +
                $"address={FormatDiagnosticAddress(nodeAddress)} <cycle/duplicate node>");
            return;
        }

        context.Builder.AppendLine(
            $"path={path} node-list-index={nodeListIndex} address={FormatDiagnosticAddress(nodeAddress)} " +
            $"id={node->NodeId} type={node->Type}({(ushort)node->Type}) " +
            $"node-flags={node->NodeFlags}(0x{(ushort)node->NodeFlags:X4}) " +
            $"visible={ReadDiagnosticVisibility(node)} child-count={node->ChildCount}");
        context.Builder.AppendLine(
            $"path={path} geometry x={node->X:R} y={node->Y:R} screen-x={node->ScreenX:R} " +
            $"screen-y={node->ScreenY:R} width={node->Width} height={node->Height} " +
            $"scale-x={node->ScaleX:R} scale-y={node->ScaleY:R} rotation={node->Rotation:R} " +
            $"origin-x={node->OriginX:R} origin-y={node->OriginY:R} depth={node->Depth:R} " +
            $"depth-2={node->Depth_2:R} priority={node->Priority}");
        context.Builder.AppendLine(
            $"path={path} color={FormatDiagnosticColor(node->Color)} alpha-2={node->Alpha_2} " +
            $"add-rgb=({node->AddRed},{node->AddGreen},{node->AddBlue}) " +
            $"add-rgb-2=({node->AddRed_2},{node->AddGreen_2},{node->AddBlue_2}) " +
            $"multiply-rgb=({node->MultiplyRed},{node->MultiplyGreen},{node->MultiplyBlue}) " +
            $"multiply-rgb-2=({node->MultiplyRed_2},{node->MultiplyGreen_2},{node->MultiplyBlue_2})");
        context.Builder.AppendLine(
            $"path={path} relationships parent={FormatDiagnosticAddress((nint)node->ParentNode)} " +
            $"prev={FormatDiagnosticAddress((nint)node->PrevSiblingNode)} " +
            $"next={FormatDiagnosticAddress((nint)node->NextSiblingNode)} " +
            $"child={FormatDiagnosticAddress((nint)node->ChildNode)}");

        if (node->Type == NodeType.Text)
        {
            var textNode = (AtkTextNode*)node;
            try
            {
                var text = textNode->NodeText.ToString();
                context.Builder.AppendLine(
                    $"path={path} text-length={text.Length} " +
                    $"text=\"{EscapeDiagnosticText(text)}\" " +
                    $"text-flags={textNode->TextFlags} font-size={textNode->FontSize} " +
                    $"text-color={FormatDiagnosticColor(textNode->TextColor)} " +
                    $"edge-color={FormatDiagnosticColor(textNode->EdgeColor)} " +
                    $"background-color={FormatDiagnosticColor(textNode->BackgroundColor)}");
            }
            catch (Exception exception)
            {
                context.Builder.AppendLine(
                    $"path={path} <text capture error: {EscapeDiagnosticText(exception.Message)}>");
            }
        }

        if ((ushort)node->Type < 1_000)
            return;

        var componentNode = (AtkComponentNode*)node;
        var component = componentNode->Component;
        if (component == null)
        {
            context.Builder.AppendLine(
                $"path={path} component=<null> <invalid component node>");
            return;
        }

        context.Builder.AppendLine(
            $"path={path} component={FormatDiagnosticAddress((nint)component)} " +
            $"component-flags=0x{component->ComponentFlags:X8} " +
            $"owner-node={FormatDiagnosticAddress((nint)component->OwnerNode)} " +
            $"res-node={FormatDiagnosticAddress((nint)component->AtkResNode)} " +
            $"sound-effect-id={component->SoundEffectId}");
        DumpDiagnosticUldManager(
            &component->UldManager,
            $"{path}/component-uld",
            depth + 1,
            context);
    }

    private static string ReadDiagnosticVisibility(AtkResNode* node)
    {
        if (node == null)
            return "<null>";
        try
        {
            return node->IsVisible().ToString();
        }
        catch (Exception exception)
        {
            return $"<error:{EscapeDiagnosticText(exception.Message)}>";
        }
    }

    private static string FormatDiagnosticAddress(nint address)
        => address == nint.Zero ? "<null>" : $"0x{address:X}";

    private static string FormatDiagnosticColor(
        FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor color)
        => $"rgba({color.R},{color.G},{color.B},{color.A})";

    private static string EscapeDiagnosticText(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    escaped.Append(@"\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\0':
                    escaped.Append(@"\0");
                    break;
                case '\a':
                    escaped.Append(@"\a");
                    break;
                case '\b':
                    escaped.Append(@"\b");
                    break;
                case '\f':
                    escaped.Append(@"\f");
                    break;
                case '\n':
                    escaped.Append(@"\n");
                    break;
                case '\r':
                    escaped.Append(@"\r");
                    break;
                case '\t':
                    escaped.Append(@"\t");
                    break;
                case '\v':
                    escaped.Append(@"\v");
                    break;
                default:
                    if (char.IsControl(character))
                        escaped.Append($"\\u{(int)character:X4}");
                    else
                        escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

    public DadAllianceNativeStep AdvanceCreate(int passcode)
    {
        RequireFrameworkThread();
        if (createFlow.RequiresMutationSafety)
        {
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
            ParticipatingInCrossWorldPartyOrAlliance:
                result.ParticipatingInCrossWorldPartyOrAlliance,
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
        var isNewJoin =
            !string.Equals(
                activeJoinKey,
                joinKey,
                StringComparison.OrdinalIgnoreCase);
        if (isNewJoin)
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
        var agent = AgentLookingForGroup.Instance();
        var isLocalCreator =
            agent != null &&
            condition[ConditionFlag.UsingPartyFinder] &&
            string.Equals(
                local.Character.CharacterName?.Trim(),
                instruction.LeaderName.Trim(),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                local.Character.WorldName?.Trim(),
                instruction.LeaderWorld.Trim(),
                StringComparison.OrdinalIgnoreCase);
        var recruitmentCondition =
            GetAddon<AtkUnitBase>("LookingForGroupCondition");
        if (!isLocalCreator &&
            (condition[ConditionFlag.UsingPartyFinder] ||
             recruitmentCondition != null &&
             recruitmentCondition->IsVisible))
        {
            return Blocked(
                "The worker unexpectedly entered Party Finder recruitment mode; this join request is blocked without retries or cleanup.");
        }
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

            if (observed == instruction.AssignedAlliance)
            {
                return new DadAllianceNativeStep(
                    DadAllianceNativeStepKind.Succeeded,
                    DadAllianceRecruitmentState.Complete,
                    $"Verified exact Alliance {observed}.",
                    0,
                    observed);
            }

            return Waiting(
                DadAllianceRecruitmentState.Verifying,
                "The Alliance-A PF creator is waiting for cross-realm subgroup verification.",
                observed);
        }

        if (observed == instruction.AssignedAlliance &&
            (isNewJoin ||
             joinFlow.Stage is
                 DadAlliancePfJoinStage.VerifyAlliance or
                 DadAlliancePfJoinStage.Complete))
        {
            return new DadAllianceNativeStep(
                DadAllianceNativeStepKind.Succeeded,
                DadAllianceRecruitmentState.Complete,
                $"Verified exact Alliance {observed}.",
                0,
                observed);
        }

        var safety = ValidateSafeMutation(requireSolo: false);
        if (!string.IsNullOrWhiteSpace(safety))
            return Waiting(DadAllianceRecruitmentState.WaitingUnsafe, safety, observed);

        if ((observed != DadAllianceAssignment.None &&
             observed != instruction.AssignedAlliance) ||
            (observed == DadAllianceAssignment.None &&
             IsInExistingParty()))
        {
            var leave = AdvanceGuardedLeave();
            if (leave.Kind != DadAllianceNativeStepKind.Succeeded)
                return leave with { ObservedAlliance = observed };
            joinFlow = new DadAlliancePartyFinderJoinFlow(this);
        }

        var result = joinFlow.Advance(new DadAlliancePfJoinTarget
        {
            LeaderName = instruction.LeaderName,
            LeaderWorld = instruction.LeaderWorld,
            TargetContentId = instruction.TargetContentId,
            AssignedAlliance = instruction.AssignedAlliance,
            Passcode = instruction.Passcode,
        });
        return new DadAllianceNativeStep(
            result.Kind switch
            {
                DadAlliancePfJoinResultKind.Progress =>
                    DadAllianceNativeStepKind.Progress,
                DadAlliancePfJoinResultKind.Waiting =>
                    DadAllianceNativeStepKind.Waiting,
                DadAlliancePfJoinResultKind.Retry =>
                    DadAllianceNativeStepKind.Retry,
                DadAlliancePfJoinResultKind.Succeeded =>
                    DadAllianceNativeStepKind.Succeeded,
                DadAlliancePfJoinResultKind.Blocked =>
                    DadAllianceNativeStepKind.Blocked,
                DadAlliancePfJoinResultKind.Stopped =>
                    DadAllianceNativeStepKind.Stopped,
                _ => DadAllianceNativeStepKind.Retry,
            },
            GetJoinState(result),
            result.Summary,
            ObservedAlliance: result.ObservedAlliance,
            CreateStage: $"Join:{result.Stage}",
            CreateEvent: result.Event,
            Attempt: result.RetryCycle,
            LastError: result.Kind is
                    DadAlliancePfJoinResultKind.Retry or
                    DadAlliancePfJoinResultKind.Blocked
                ? result.Summary
                : string.Empty,
            Readiness: $"retry-cycle={result.RetryCycle}; listing-index={result.ListingIndex}",
            ShouldAudit: result.ShouldAudit);
    }

    DadAlliancePfJoinSnapshot IDadAlliancePartyFinderJoinUi.Read(
        DadAlliancePfJoinTarget target)
    {
        var agent = AgentLookingForGroup.Instance();
        var main = GetAddon<AddonLookingForGroup>("LookingForGroup");
        var detailAddon =
            GetAddon<AddonLookingForGroupDetail>("LookingForGroupDetail");
        var yesNo = GetAddon<AddonSelectYesno>("SelectYesno");
        var privatePrompt =
            GetAddon<AtkUnitBase>("LookingForGroupPrivate");
        var recruitmentCondition =
            GetAddon<AtkUnitBase>("LookingForGroupCondition");
        var mainBase = main == null
            ? null
            : &main->AddonLookingForGroupBase.AtkUnitBase;
        var detailBase = detailAddon == null
            ? null
            : &detailAddon->AtkUnitBase;
        var mainVisible = mainBase != null && mainBase->IsVisible;
        var detailVisible = detailBase != null && detailBase->IsVisible;
        var yesNoVisible =
            yesNo != null && yesNo->AtkUnitBase.IsVisible;
        var privateVisible =
            privatePrompt != null && privatePrompt->IsVisible;
        var recruitmentConditionVisible =
            recruitmentCondition != null &&
            recruitmentCondition->IsVisible;
        var detail = agent == null
            ? default
            : agent->LastViewedListing;
        var yesNoText =
            yesNoVisible && yesNo->PromptText != null
                ? yesNo->PromptText->NodeText.ToString().Trim()
                : string.Empty;
        var joinFlags = detail.JoinConditionFlags;
        var numberOfListings =
            agent == null ? 0 : agent->NumberOfListingsDisplayed;
        var matchingListingIndexes =
            main == null
                ? []
                : DadAlliancePartyFinderListingRowResolver.Resolve(
                    target.LeaderName,
                    numberOfListings,
                    ReadListingView(main->StandardViewList),
                    ReadListingView(main->CompactViewList));

        return new DadAlliancePfJoinSnapshot
        {
            AgentAvailable = agent != null,
            MainVisible = mainVisible,
            MainReady = mainVisible && mainBase->IsReady,
            RecruitmentConditionVisible =
                recruitmentConditionVisible,
            RecruitmentConditionReady =
                recruitmentConditionVisible &&
                recruitmentCondition->IsReady,
            WorkerRecruiting =
                condition[ConditionFlag.UsingPartyFinder],
            SearchAreaTab = agent == null ? (byte)0 : agent->SearchAreaTab,
            CategoryTab = agent == null ? (byte)0 : agent->CategoryTab,
            NumberOfListings = numberOfListings,
            MatchingListingIndexes = matchingListingIndexes,
            DetailVisible = detailVisible,
            DetailReady = detailVisible && detailBase->IsReady,
            DetailLeaderName = detailVisible
                ? detail.LeaderString.Trim()
                : string.Empty,
            DetailLeaderWorld = detailVisible
                ? ResolveWorldName(detail.HomeWorld)
                : string.Empty,
            DetailDutyId = detailVisible ? detail.DutyId : (ushort)0,
            DetailPrivate =
                detailVisible &&
                (joinFlags & AgentLookingForGroup.JoinCondition.Private) != 0,
            DetailAlliance =
                detailVisible &&
                detail.IsAlliance &&
                (joinFlags &
                 AgentLookingForGroup.JoinCondition.AllianceRaid) != 0,
            DetailPartyCount =
                detailVisible ? detail.NumberOfParties : 0,
            YesNoVisible = yesNoVisible,
            YesNoReady = yesNoVisible && yesNo->AtkUnitBase.IsReady,
            YesNoIdentity = yesNoVisible
                ? $"{(nint)yesNo:X}:{yesNoText}"
                : string.Empty,
            PrivatePromptVisible = privateVisible,
            PrivatePromptReady =
                privateVisible && privatePrompt->IsReady,
            ObservedAlliance = ObserveAlliance(target.TargetContentId),
        };
    }

    private static DadAlliancePfListingViewSnapshot ReadListingView(
        AtkComponentList* list)
    {
        if (list == null)
            return new DadAlliancePfListingViewSnapshot();

        var root = list->AtkResNode;
        var visible = root != null && root->IsVisible();
        var rendererStorageReady =
            list->AllocatedItemRendererListLength == 0 ||
            list->ItemRendererList != null;
        var ready =
            visible &&
            list->UldManager.LoadedState == AtkLoadState.Loaded &&
            list->ListLength >= 0 &&
            list->AllocatedItemRendererListLength >= 0 &&
            rendererStorageReady;
        if (!ready)
        {
            return new DadAlliancePfListingViewSnapshot
            {
                Available = true,
                Visible = visible,
                Ready = false,
                ListLength = Math.Max(0, list->ListLength),
            };
        }

        var renderers =
            new List<DadAlliancePfListingRendererSnapshot>();
        var rendererCount = Math.Min(
            list->AllocatedItemRendererListLength,
            MaxListingRendererScan);
        for (var storageIndex = 0;
             storageIndex < rendererCount;
             storageIndex++)
        {
            var renderer =
                list->ItemRendererList[storageIndex]
                    .AtkComponentListItemRenderer;
            if (renderer == null ||
                renderer->AtkResNode == null ||
                !renderer->AtkResNode->IsVisible())
            {
                continue;
            }

            renderers.Add(new DadAlliancePfListingRendererSnapshot(
                renderer->ListItemIndex,
                ReadListingRendererTexts(renderer)));
        }

        return new DadAlliancePfListingViewSnapshot
        {
            Available = true,
            Visible = true,
            Ready = true,
            ListLength = list->ListLength,
            Renderers = renderers,
        };
    }

    private static IReadOnlyList<string> ReadListingRendererTexts(
        AtkComponentListItemRenderer* renderer)
    {
        var texts = new List<string>();
        var visitedComponents = new HashSet<nint>();
        var remainingNodes = MaxListingTreeNodes;
        ReadListingTreeTexts(
            &renderer->UldManager,
            0,
            visitedComponents,
            texts,
            ref remainingNodes);
        return texts;
    }

    private static void ReadListingTreeTexts(
        AtkUldManager* manager,
        int depth,
        HashSet<nint> visitedComponents,
        List<string> texts,
        ref int remainingNodes)
    {
        if (manager == null ||
            depth > MaxListingTreeDepth ||
            remainingNodes <= 0 ||
            manager->NodeList == null)
        {
            return;
        }

        var nodeCount = Math.Min(
            manager->NodeListCount,
            (ushort)Math.Min(remainingNodes, ushort.MaxValue));
        for (var nodeIndex = 0;
             nodeIndex < nodeCount && remainingNodes > 0;
             nodeIndex++)
        {
            remainingNodes--;
            var node = manager->NodeList[nodeIndex];
            if (node == null)
                continue;

            if (node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString().Trim();
                if (text.Length > 0)
                    texts.Add(text);
                continue;
            }

            if ((ushort)node->Type < 1000)
                continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null ||
                !visitedComponents.Add((nint)component))
            {
                continue;
            }

            ReadListingTreeTexts(
                &component->UldManager,
                depth + 1,
                visitedComponents,
                texts,
                ref remainingNodes);
        }
    }

    DadAlliancePfJoinActionResult IDadAlliancePartyFinderJoinUi.Perform(
        DadAlliancePfJoinActionRequest request)
    {
        if (request.Action == DadAlliancePfJoinAction.Show)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
            {
                return new DadAlliancePfJoinActionResult(
                    false,
                    "Party Finder cannot be shown.",
                    "Party Finder agent is unavailable.");
            }

            var main = GetAddon<AddonLookingForGroup>("LookingForGroup");
            if (main != null &&
                main->AddonLookingForGroupBase.AtkUnitBase.IsVisible)
            {
                return new DadAlliancePfJoinActionResult(
                    true,
                    "Party Finder became visible before Show; no toggle was sent.");
            }

            agent->Show();
            return new DadAlliancePfJoinActionResult(
                true,
                "Requested Party Finder Show once while its window was hidden.");
        }

        IReadOnlyList<DadAlliancePfJoinCallback> callbacks;
        try
        {
            callbacks = DadAlliancePartyFinderJoinCallbacks.Build(request);
        }
        catch (Exception exception)
        {
            return new DadAlliancePfJoinActionResult(
                false,
                $"{request.Action} callback plan is invalid.",
                exception.Message);
        }

        if (callbacks.Count == 0)
        {
            return new DadAlliancePfJoinActionResult(
                false,
                $"{request.Action} has no callback plan.");
        }

        var dispatch = joinCallbackDispatcher.TryDispatch(
            request.Action,
            callbacks,
            addonName => ResolveReadyJoinCallbackAddon(
                addonName,
                request.Action));
        if (!dispatch.Sent)
        {
            return new DadAlliancePfJoinActionResult(
                false,
                $"{request.Action} callback sequence failed.",
                dispatch.Error);
        }

        return new DadAlliancePfJoinActionResult(
            true,
            DescribeJoinAction(request));
    }

    private static DadAllianceRecruitmentState GetJoinState(
        DadAlliancePfJoinResult result)
        => result.Kind switch
        {
            DadAlliancePfJoinResultKind.Succeeded =>
                DadAllianceRecruitmentState.Complete,
            DadAlliancePfJoinResultKind.Blocked =>
                DadAllianceRecruitmentState.Blocked,
            DadAlliancePfJoinResultKind.Stopped =>
                DadAllianceRecruitmentState.Stopped,
            DadAlliancePfJoinResultKind.Retry =>
                DadAllianceRecruitmentState.RetryWaiting,
            _ when result.Stage is
                DadAlliancePfJoinStage.SelectAlliance or
                DadAlliancePfJoinStage.WaitYesNo or
                DadAlliancePfJoinStage.ConfirmYes or
                DadAlliancePfJoinStage.WaitPrivatePrompt or
                DadAlliancePfJoinStage.SubmitPasscode or
                DadAlliancePfJoinStage.WaitPasscodeAcknowledged or
                DadAlliancePfJoinStage.CloseJoinedDetail or
                DadAlliancePfJoinStage.WaitJoinedDetailClosed =>
                DadAllianceRecruitmentState.Joining,
            _ when result.Stage == DadAlliancePfJoinStage.VerifyAlliance =>
                DadAllianceRecruitmentState.Verifying,
            _ => DadAllianceRecruitmentState.Searching,
        };

    private static AtkUnitBase* GetJoinCallbackAddon(string name)
    {
        if (string.Equals(
                name,
                "LookingForGroup",
                StringComparison.Ordinal))
        {
            var main = GetAddon<AddonLookingForGroup>(name);
            return main == null
                ? null
                : &main->AddonLookingForGroupBase.AtkUnitBase;
        }

        return GetAddon<AtkUnitBase>(name);
    }

    private static nint ResolveReadyJoinCallbackAddon(
        string name,
        DadAlliancePfJoinAction action)
    {
        var addon = GetJoinCallbackAddon(name);
        if (addon == null || !addon->IsReady)
            return nint.Zero;
        if (RequiresVisibleJoinAddon(name, action) &&
            !addon->IsVisible)
        {
            return nint.Zero;
        }

        return (nint)addon;
    }

    private static bool RequiresVisibleJoinAddon(
        string name,
        DadAlliancePfJoinAction action)
        => (string.Equals(
                name,
                "LookingForGroup",
                StringComparison.Ordinal) &&
            action != DadAlliancePfJoinAction.SelectAlliance) ||
           string.Equals(
               name,
               "SelectYesno",
               StringComparison.Ordinal) ||
           string.Equals(
               name,
               "LookingForGroupPrivate",
               StringComparison.Ordinal) ||
           string.Equals(
               name,
               "LookingForGroupDetail",
               StringComparison.Ordinal);

    private static string DescribeJoinAction(
        DadAlliancePfJoinActionRequest request)
        => request.Action switch
        {
            DadAlliancePfJoinAction.SelectPrivate =>
                "Selected Private search tab 2 once.",
            DadAlliancePfJoinAction.SelectRaids =>
                "Selected Raids category index 5 once.",
            DadAlliancePfJoinAction.Refresh =>
                "Refreshed Private Raids results once for this retry cycle.",
            DadAlliancePfJoinAction.OpenListing =>
                $"Requested result index {request.ListingIndex} with ordered callbacks 13 then 11 once.",
            DadAlliancePfJoinAction.CloseDetail =>
                "Sent detail close callback -2 once.",
            DadAlliancePfJoinAction.SelectAlliance =>
                $"Selected Alliance {request.Alliance} once.",
            DadAlliancePfJoinAction.ConfirmYes =>
                "Clicked Yes once on the acknowledged fresh subgroup confirmation.",
            DadAlliancePfJoinAction.SubmitPasscode =>
                "Submitted the four-digit private passcode once.",
            _ => request.Action.ToString(),
        };

    private void LogJoinCallbackTrace(
        DadAlliancePfNativeCallbackTrace callbackTrace)
        => log.Information(
            "[dad][AlliancePF] native-callback {Phase} action={Action} addon={Addon} ordinal={Ordinal}/{Total} payload-types={PayloadTypes} update-state={UpdateState}",
            callbackTrace.Phase,
            callbackTrace.Action,
            callbackTrace.Addon,
            callbackTrace.Ordinal,
            callbackTrace.Total,
            callbackTrace.PayloadTypes,
            callbackTrace.UpdateState);

    public DadAllianceNativeStep AdvanceEndRecruitment(bool dadOwnsRecruitment)
    {
        RequireFrameworkThread();
        var safety = ValidateSafeMutation(requireSolo: false, allowParty: true);
        if (!string.IsNullOrWhiteSpace(safety))
            return Waiting(DadAllianceRecruitmentState.WaitingUnsafe, safety);

        var result = cleanupFlow.Advance(dadOwnsRecruitment);
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

    public void RestartCreateCycle()
    {
        RequireFrameworkThread();
        StopCreate();
        createUi.ResetErrors();
        createFlow = new DadAlliancePartyFinderCreateFlow(createUi);
    }

    public void StopJoin()
    {
        RequireFrameworkThread();
        joinFlow.Stop();
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
        joinFlow = new DadAlliancePartyFinderJoinFlow(this);
        leavePromptBaseline = string.Empty;
        leaveRequested = false;
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

    private sealed class LookingForGroupDiagnosticCaptureContext
    {
        public StringBuilder Builder { get; } = new(64 * 1_024);
        public HashSet<nint> SeenManagers { get; } = [];
        public HashSet<nint> SeenNodes { get; } = [];
        public int RendererSlots { get; set; }
        public int NodeEntries { get; set; }
        public bool RendererTruncated { get; set; }
        public bool NodeTruncated { get; set; }
        public bool DepthTruncated { get; set; }
    }

    private DadAllianceNativeStep Progress(
        string summary,
        DadAllianceRecruitmentState state = DadAllianceRecruitmentState.CreatingListing,
        DadAllianceAssignment observed = DadAllianceAssignment.None)
        => new(DadAllianceNativeStepKind.Progress, state, summary, 0, observed);

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
