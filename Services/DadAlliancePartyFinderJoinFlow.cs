using dad.Models;

namespace dad.Services;

internal enum DadAlliancePfJoinStage
{
    EnsureWindow,
    WaitWindow,
    SelectPrivate,
    WaitPrivate,
    SelectRaids,
    WaitRaids,
    Refresh,
    WaitRefresh,
    InspectListings,
    WaitDetail,
    CloseMismatchedDetail,
    WaitMismatchedDetailClosed,
    SelectAlliance,
    WaitYesNo,
    ConfirmYes,
    WaitPrivatePrompt,
    SubmitPasscode,
    WaitPasscodeAcknowledged,
    CloseJoinedDetail,
    WaitJoinedDetailClosed,
    VerifyAlliance,
    CloseForRetry,
    WaitRetryDetailClosed,
    Complete,
    Blocked,
    Stopped,
}

internal enum DadAlliancePfJoinAction
{
    Show,
    SelectPrivate,
    SelectRaids,
    Refresh,
    OpenListing,
    CloseDetail,
    SelectAlliance,
    ConfirmYes,
    SubmitPasscode,
}

internal enum DadAlliancePfJoinResultKind
{
    Progress,
    Waiting,
    Retry,
    Succeeded,
    Blocked,
    Stopped,
}

internal sealed record DadAlliancePfJoinTarget
{
    public string LeaderName { get; init; } = string.Empty;
    public string LeaderWorld { get; init; } = string.Empty;
    public ulong TargetContentId { get; init; }
    public DadAllianceAssignment AssignedAlliance { get; init; }
    public int Passcode { get; init; }
}

internal sealed record DadAlliancePfJoinSnapshot
{
    public bool AgentAvailable { get; init; } = true;
    public bool MainVisible { get; init; }
    public bool MainReady { get; init; }
    public bool RecruitmentConditionVisible { get; init; }
    public bool RecruitmentConditionReady { get; init; }
    public bool WorkerRecruiting { get; init; }
    public byte SearchAreaTab { get; init; }
    public byte CategoryTab { get; init; }
    public int NumberOfListings { get; init; }
    public IReadOnlyList<int> MatchingListingIndexes { get; init; } = [];
    public bool DetailVisible { get; init; }
    public bool DetailReady { get; init; }
    public string DetailLeaderName { get; init; } = string.Empty;
    public string DetailLeaderWorld { get; init; } = string.Empty;
    public ushort DetailDutyId { get; init; }
    public bool DetailPrivate { get; init; }
    public bool DetailAlliance { get; init; }
    public int DetailPartyCount { get; init; }
    public bool YesNoVisible { get; init; }
    public bool YesNoReady { get; init; }
    public string YesNoIdentity { get; init; } = string.Empty;
    public bool PrivatePromptVisible { get; init; }
    public bool PrivatePromptReady { get; init; }
    public DadAllianceAssignment ObservedAlliance { get; init; }
}

internal sealed record DadAlliancePfListingRendererSnapshot(
    int ListItemIndex,
    string? RecruiterName);

internal sealed record DadAlliancePfListingViewSnapshot
{
    public bool Available { get; init; }
    public bool Visible { get; init; }
    public bool Ready { get; init; }
    public int ListLength { get; init; }
    public IReadOnlyList<DadAlliancePfListingRendererSnapshot> Renderers { get; init; } = [];
}

internal static class DadAlliancePartyFinderListingRowResolver
{
    public static IReadOnlyList<int> Resolve(
        string coordinatorName,
        int numberOfListingsDisplayed,
        DadAlliancePfListingViewSnapshot standardView,
        DadAlliancePfListingViewSnapshot compactView)
    {
        if (coordinatorName.Length == 0 ||
            numberOfListingsDisplayed <= 0)
            return [];

        var standardReady = IsUsable(standardView);
        var compactReady = IsUsable(compactView);
        if (standardReady == compactReady)
            return [];

        var selected = standardReady ? standardView : compactView;
        var listingBound = Math.Min(
            numberOfListingsDisplayed,
            Math.Max(0, selected.ListLength));
        if (listingBound == 0)
            return [];

        return selected.Renderers
            .Where(renderer =>
                renderer.ListItemIndex >= 0 &&
                renderer.ListItemIndex < listingBound &&
                string.Equals(
                    coordinatorName,
                    renderer.RecruiterName,
                    StringComparison.Ordinal))
            .Select(static renderer => renderer.ListItemIndex)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static bool IsUsable(DadAlliancePfListingViewSnapshot view)
        => view.Available && view.Visible && view.Ready;
}

internal readonly record struct DadAlliancePfJoinActionRequest(
    DadAlliancePfJoinAction Action,
    int ListingIndex = -1,
    DadAllianceAssignment Alliance = DadAllianceAssignment.None,
    int Passcode = 0);

internal readonly record struct DadAlliancePfJoinActionResult(
    bool Sent,
    string Summary,
    string Error = "");

internal interface IDadAlliancePartyFinderJoinUi
{
    DadAlliancePfJoinSnapshot Read(DadAlliancePfJoinTarget target);
    DadAlliancePfJoinActionResult Perform(DadAlliancePfJoinActionRequest request);
}

internal readonly record struct DadAlliancePfJoinResult(
    DadAlliancePfJoinResultKind Kind,
    DadAlliancePfJoinStage Stage,
    string Event,
    string Summary,
    int RetryCycle,
    int ListingIndex,
    DadAllianceAssignment ObservedAlliance,
    bool ShouldAudit);

internal readonly record struct DadAlliancePfJoinCallback(
    string Addon,
    bool UpdateState,
    object[] Values);

internal static class DadAlliancePartyFinderJoinCallbacks
{
    public const byte PrivateSearchAreaTab = 2;
    public const byte RaidsCategoryIndex = 5;

    public static IReadOnlyList<DadAlliancePfJoinCallback> Build(
        DadAlliancePfJoinActionRequest request)
        => request.Action switch
        {
            DadAlliancePfJoinAction.SelectPrivate =>
            [
                new("LookingForGroup", true, [20, (int)PrivateSearchAreaTab]),
            ],
            DadAlliancePfJoinAction.SelectRaids =>
            [
                new("LookingForGroup", true, [21, (int)RaidsCategoryIndex]),
            ],
            DadAlliancePfJoinAction.Refresh =>
            [
                new("LookingForGroup", true, [17]),
            ],
            DadAlliancePfJoinAction.OpenListing when request.ListingIndex >= 0 =>
            [
                new("LookingForGroup", true, [13, request.ListingIndex]),
                new("LookingForGroup", true, [11, request.ListingIndex]),
            ],
            DadAlliancePfJoinAction.CloseDetail =>
            [
                new("LookingForGroupDetail", false, [-2]),
            ],
            DadAlliancePfJoinAction.SelectAlliance =>
            [
                BuildAllianceCallback(request.Alliance),
            ],
            DadAlliancePfJoinAction.ConfirmYes =>
            [
                new("SelectYesno", true, [0]),
            ],
            DadAlliancePfJoinAction.SubmitPasscode
                when request.Passcode is >= 1000 and <= 9999 =>
            [
                new("LookingForGroupPrivate", true, [0, request.Passcode]),
            ],
            _ => [],
        };

    private static DadAlliancePfJoinCallback BuildAllianceCallback(
        DadAllianceAssignment alliance)
    {
        var index = DadAlliancePartyFinderRules.GetJoinAllianceButtonIndex(alliance);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alliance),
                alliance,
                "A concrete alliance assignment is required.");
        }

        return new(
            "LookingForGroupDetail",
            true,
            [12 + index, $"Alliance {alliance}"]);
    }
}

/// <summary>
/// Pure acknowledgement-driven Private Party Finder join coordinator.
/// Every callback is dispatched once and only a later observed snapshot advances it.
/// </summary>
internal sealed class DadAlliancePartyFinderJoinFlow
{
    internal const ushort LabyrinthDutyId =
        DadAlliancePartyFinderPresetDefinition.LabyrinthDutyId;
    internal static readonly TimeSpan ObservationTimeout =
        TimeSpan.FromSeconds(5);

    private readonly IDadAlliancePartyFinderJoinUi ui;
    private readonly Func<DateTime> utcNow;
    private DadAlliancePfJoinStage stage = DadAlliancePfJoinStage.EnsureWindow;
    private DateTime deadlineUtc = DateTime.MinValue;
    private int retryCycle = 1;
    private int listingCursor;
    private int listingCount;
    private string freshYesNoIdentity = string.Empty;
    private string retryReason = string.Empty;
    private bool currentDetailCloseDispatched;
    private bool stopped;

    public DadAlliancePartyFinderJoinFlow(
        IDadAlliancePartyFinderJoinUi ui,
        Func<DateTime>? utcNow = null)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public DadAlliancePfJoinStage Stage => stage;

    public DadAlliancePfJoinResult Advance(DadAlliancePfJoinTarget target)
    {
        var now = EnsureUtc(utcNow());
        if (stopped)
            return Result(
                DadAlliancePfJoinResultKind.Stopped,
                "stop",
                "Private Party Finder joining stopped.",
                DadAllianceAssignment.None);
        if (stage == DadAlliancePfJoinStage.Blocked)
        {
            return Result(
                DadAlliancePfJoinResultKind.Blocked,
                "unexpected-recruitment-blocked",
                "The worker join request remains blocked after unexpected Party Finder recruitment state.",
                DadAllianceAssignment.None);
        }

        DadAlliancePfJoinSnapshot snapshot;
        try
        {
            snapshot = ui.Read(target);
        }
        catch (Exception exception)
        {
            retryReason =
                $"Party Finder observation failed; the worker will retry: {exception.Message}";
            return CompleteRetry(new DadAlliancePfJoinSnapshot());
        }
        if (snapshot.WorkerRecruiting ||
            snapshot.RecruitmentConditionVisible)
        {
            stage = DadAlliancePfJoinStage.Blocked;
            return Result(
                DadAlliancePfJoinResultKind.Blocked,
                "unexpected-recruitment-blocked",
                "The worker unexpectedly entered Party Finder recruitment mode; the join request was blocked without retries or cleanup.",
                snapshot.ObservedAlliance,
                shouldAudit: true);
        }

        if (snapshot.ObservedAlliance == target.AssignedAlliance &&
            stage == DadAlliancePfJoinStage.VerifyAlliance)
        {
            stage = DadAlliancePfJoinStage.Complete;
            return Result(
                DadAlliancePfJoinResultKind.Succeeded,
                "subgroup-acknowledged",
                $"Verified exact Alliance {target.AssignedAlliance} subgroup membership.",
                snapshot.ObservedAlliance,
                shouldAudit: true);
        }

        if (stage == DadAlliancePfJoinStage.Complete)
        {
            return Result(
                DadAlliancePfJoinResultKind.Succeeded,
                "subgroup-acknowledged",
                $"Verified exact Alliance {target.AssignedAlliance} subgroup membership.",
                snapshot.ObservedAlliance);
        }

        if (!snapshot.AgentAvailable)
        {
            return BeginRetry(
                now,
                snapshot,
                "Party Finder agent is unavailable; the worker will retry.");
        }

        return stage switch
        {
            DadAlliancePfJoinStage.EnsureWindow =>
                AdvanceEnsureWindow(target, snapshot, now),
            DadAlliancePfJoinStage.WaitWindow =>
                AdvanceWaitWindow(snapshot, now),
            DadAlliancePfJoinStage.SelectPrivate =>
                AdvanceSelectPrivate(snapshot, now),
            DadAlliancePfJoinStage.WaitPrivate =>
                AdvanceWaitPrivate(snapshot, now),
            DadAlliancePfJoinStage.SelectRaids =>
                AdvanceSelectRaids(snapshot, now),
            DadAlliancePfJoinStage.WaitRaids =>
                AdvanceWaitRaids(snapshot, now),
            DadAlliancePfJoinStage.Refresh =>
                AdvanceRefresh(snapshot, now),
            DadAlliancePfJoinStage.WaitRefresh =>
                AdvanceWaitRefresh(snapshot, now),
            DadAlliancePfJoinStage.InspectListings =>
                AdvanceInspectListings(snapshot, now),
            DadAlliancePfJoinStage.WaitDetail =>
                AdvanceWaitDetail(target, snapshot, now),
            DadAlliancePfJoinStage.CloseMismatchedDetail =>
                AdvanceCloseMismatchedDetail(snapshot, now),
            DadAlliancePfJoinStage.WaitMismatchedDetailClosed =>
                AdvanceWaitMismatchedDetailClosed(snapshot, now),
            DadAlliancePfJoinStage.SelectAlliance =>
                AdvanceSelectAlliance(target, snapshot, now),
            DadAlliancePfJoinStage.WaitYesNo =>
                AdvanceWaitYesNo(snapshot, now),
            DadAlliancePfJoinStage.ConfirmYes =>
                AdvanceConfirmYes(snapshot, now),
            DadAlliancePfJoinStage.WaitPrivatePrompt =>
                AdvanceWaitPrivatePrompt(snapshot, now),
            DadAlliancePfJoinStage.SubmitPasscode =>
                AdvanceSubmitPasscode(target, snapshot, now),
            DadAlliancePfJoinStage.WaitPasscodeAcknowledged =>
                AdvanceWaitPasscodeAcknowledged(snapshot, now),
            DadAlliancePfJoinStage.CloseJoinedDetail =>
                AdvanceCloseJoinedDetail(snapshot, now),
            DadAlliancePfJoinStage.WaitJoinedDetailClosed =>
                AdvanceWaitJoinedDetailClosed(snapshot, now),
            DadAlliancePfJoinStage.VerifyAlliance =>
                AdvanceVerifyAlliance(target, snapshot, now),
            DadAlliancePfJoinStage.CloseForRetry =>
                AdvanceCloseForRetry(snapshot, now),
            DadAlliancePfJoinStage.WaitRetryDetailClosed =>
                AdvanceWaitRetryDetailClosed(snapshot, now),
            DadAlliancePfJoinStage.Stopped =>
                Result(
                    DadAlliancePfJoinResultKind.Stopped,
                    "stop",
                    "Private Party Finder joining stopped.",
                    snapshot.ObservedAlliance),
            DadAlliancePfJoinStage.Blocked =>
                Result(
                    DadAlliancePfJoinResultKind.Blocked,
                    "unexpected-recruitment-blocked",
                    "The worker join request remains blocked after unexpected Party Finder recruitment state.",
                    snapshot.ObservedAlliance),
            _ => BeginRetry(
                now,
                snapshot,
                "Private Party Finder join state was not recognized; the worker will retry."),
        };
    }

    public void Stop()
    {
        stopped = true;
        stage = DadAlliancePfJoinStage.Stopped;
    }

    private DadAlliancePfJoinResult AdvanceEnsureWindow(
        DadAlliancePfJoinTarget target,
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.YesNoVisible || snapshot.PrivatePromptVisible)
        {
            return BeginRetry(
                now,
                snapshot,
                "A stale Party Finder confirmation is visible; DAD will not click it.");
        }
        if (snapshot.DetailVisible)
        {
            retryReason = "Closed a stale Party Finder detail before starting a fresh search cycle.";
            stage = DadAlliancePfJoinStage.CloseForRetry;
            return AdvanceCloseForRetry(snapshot, now);
        }
        if (snapshot.MainVisible)
        {
            stage = DadAlliancePfJoinStage.SelectPrivate;
            return Result(
                DadAlliancePfJoinResultKind.Progress,
                "window-acknowledged",
                "Party Finder is visible without toggling it.",
                snapshot.ObservedAlliance,
                shouldAudit: true);
        }

        return Send(
            new DadAlliancePfJoinActionRequest(DadAlliancePfJoinAction.Show),
            DadAlliancePfJoinStage.WaitWindow,
            now,
            snapshot,
            "show-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitWindow(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.MainVisible && snapshot.MainReady)
        {
            stage = DadAlliancePfJoinStage.SelectPrivate;
            return Acknowledged(
                "window-acknowledged",
                "Acknowledged the newly visible Party Finder window.",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for Party Finder to become visible after its one Show request.",
            "Party Finder did not acknowledge its one Show request.");
    }

    private DadAlliancePfJoinResult AdvanceSelectPrivate(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.MainVisible || !snapshot.MainReady)
            return BeginRetry(now, snapshot, "Party Finder became unavailable before Private tab selection.");
        if (snapshot.SearchAreaTab ==
            DadAlliancePartyFinderJoinCallbacks.PrivateSearchAreaTab)
        {
            stage = DadAlliancePfJoinStage.SelectRaids;
            return Acknowledged(
                "private-tab-acknowledged",
                "Private Party Finder search tab 2 is selected.",
                snapshot);
        }

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectPrivate),
            DadAlliancePfJoinStage.WaitPrivate,
            now,
            snapshot,
            "private-tab-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitPrivate(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.MainVisible &&
            snapshot.MainReady &&
            snapshot.SearchAreaTab ==
            DadAlliancePartyFinderJoinCallbacks.PrivateSearchAreaTab)
        {
            stage = DadAlliancePfJoinStage.SelectRaids;
            return Acknowledged(
                "private-tab-acknowledged",
                "Acknowledged Private Party Finder search tab 2.",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for Private Party Finder search tab 2 acknowledgement.",
            "Private Party Finder search tab 2 was not acknowledged.");
    }

    private DadAlliancePfJoinResult AdvanceSelectRaids(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!HasPrivateMain(snapshot))
            return BeginRetry(now, snapshot, "Private Party Finder search state was lost before Raids selection.");
        if (snapshot.CategoryTab ==
            DadAlliancePartyFinderJoinCallbacks.RaidsCategoryIndex)
        {
            stage = DadAlliancePfJoinStage.Refresh;
            return Acknowledged(
                "raids-acknowledged",
                "Raids category index 5 is selected.",
                snapshot);
        }

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectRaids),
            DadAlliancePfJoinStage.WaitRaids,
            now,
            snapshot,
            "raids-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitRaids(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (HasPrivateMain(snapshot) &&
            snapshot.CategoryTab ==
            DadAlliancePartyFinderJoinCallbacks.RaidsCategoryIndex)
        {
            stage = DadAlliancePfJoinStage.Refresh;
            return Acknowledged(
                "raids-acknowledged",
                "Acknowledged Raids category index 5.",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for Raids category index 5 acknowledgement.",
            "Raids category index 5 was not acknowledged.");
    }

    private DadAlliancePfJoinResult AdvanceRefresh(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!HasPrivateRaidsMain(snapshot))
            return BeginRetry(now, snapshot, "Private Raids search state was lost before refresh.");

        return Send(
            new DadAlliancePfJoinActionRequest(DadAlliancePfJoinAction.Refresh),
            DadAlliancePfJoinStage.WaitRefresh,
            now,
            snapshot,
            "refresh-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitRefresh(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (HasPrivateRaidsMain(snapshot) && snapshot.NumberOfListings > 0)
        {
            listingCursor = 0;
            listingCount = snapshot.NumberOfListings;
            stage = DadAlliancePfJoinStage.InspectListings;
            return Acknowledged(
                "refresh-acknowledged",
                $"Acknowledged one Private Raids refresh with {listingCount} visible result(s).",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for the one Private Raids refresh to expose results.",
            "The one Private Raids refresh exposed no results.");
    }

    private DadAlliancePfJoinResult AdvanceInspectListings(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.DetailVisible)
        {
            stage = DadAlliancePfJoinStage.CloseMismatchedDetail;
            return Acknowledged(
                "stale-detail-observed",
                "A stale Party Finder detail will be closed before inspecting the next result.",
                snapshot);
        }
        if (!HasPrivateRaidsMain(snapshot))
            return BeginRetry(now, snapshot, "Private Raids search state was lost while inspecting results.");
        if (listingCursor >= listingCount)
        {
            return BeginRetry(
                now,
                snapshot,
                "No exact private Labyrinth listing was found in this refresh cycle.");
        }

        var matchingIndex = snapshot.MatchingListingIndexes
            .Where(index => index >= listingCursor && index < listingCount)
            .DefaultIfEmpty(-1)
            .Min();
        if (matchingIndex < 0)
        {
            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for an exact coordinator-name Party Finder row to hydrate; no listing callback has been sent.",
                "No exact coordinator-name Party Finder row was found in this refresh cycle.");
        }

        listingCursor = matchingIndex;
        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.OpenListing,
                ListingIndex: listingCursor),
            DadAlliancePfJoinStage.WaitDetail,
            now,
            snapshot,
            "listing-open-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitDetail(
        DadAlliancePfJoinTarget target,
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.DetailVisible && snapshot.DetailReady)
        {
            if (!IsExactListing(target, snapshot))
            {
                stage = DadAlliancePfJoinStage.CloseMismatchedDetail;
                return Acknowledged(
                    "listing-rejected",
                    $"Rejected result index {listingCursor} because its detail does not match the exact private Labyrinth alliance listing.",
                    snapshot);
            }

            stage = DadAlliancePfJoinStage.SelectAlliance;
            deadlineUtc = now + ObservationTimeout;
            freshYesNoIdentity = string.Empty;
            return Acknowledged(
                "listing-acknowledged",
                $"Acknowledged exact private Labyrinth alliance result index {listingCursor}.",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            $"Waiting for detail acknowledgement of result index {listingCursor}; the result cursor is retained.",
            $"Result index {listingCursor} did not expose an acknowledged detail.");
    }

    private DadAlliancePfJoinResult AdvanceCloseMismatchedDetail(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.DetailVisible)
        {
            listingCursor++;
            currentDetailCloseDispatched = false;
            stage = DadAlliancePfJoinStage.InspectListings;
            return Acknowledged(
                "detail-close-acknowledged",
                "Acknowledged closed mismatched detail and advanced the result cursor once.",
                snapshot);
        }

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.CloseDetail),
            DadAlliancePfJoinStage.WaitMismatchedDetailClosed,
            now,
            snapshot,
            "detail-close-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitMismatchedDetailClosed(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.DetailVisible)
        {
            listingCursor++;
            currentDetailCloseDispatched = false;
            stage = DadAlliancePfJoinStage.InspectListings;
            return Acknowledged(
                "detail-close-acknowledged",
                "Acknowledged closed mismatched detail and advanced the result cursor once.",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for mismatched Party Finder detail to close.",
            "Mismatched Party Finder detail did not close after its single callback.");
    }

    private DadAlliancePfJoinResult AdvanceSelectAlliance(
        DadAlliancePfJoinTarget target,
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.DetailVisible || !snapshot.DetailReady)
            return BeginRetry(now, snapshot, "The exact Party Finder detail closed before subgroup selection.");
        if (!IsExactListing(target, snapshot))
        {
            return BeginRetry(
                now,
                snapshot,
                "The exact Party Finder detail changed before subgroup selection; Alliance was not dispatched.");
        }
        if (snapshot.YesNoVisible || snapshot.PrivatePromptVisible)
        {
            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for a pre-existing confirmation or private prompt to disappear; DAD will not click it.",
                "A pre-existing confirmation or private prompt remained visible; the worker will retry without clicking it.");
        }

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectAlliance,
                Alliance: target.AssignedAlliance),
            DadAlliancePfJoinStage.WaitYesNo,
            now,
            snapshot,
            "alliance-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitYesNo(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.YesNoVisible && snapshot.PrivatePromptVisible)
        {
            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for the simultaneous subgroup confirmation and private prompt to resolve without a callback.",
                "Simultaneous subgroup confirmation and private prompts remained visible; the worker will retry without clicking either.");
        }

        if (snapshot.YesNoVisible)
        {
            if (snapshot.YesNoReady &&
                !string.IsNullOrWhiteSpace(snapshot.YesNoIdentity) &&
                !string.Equals(
                    snapshot.YesNoIdentity,
                    freshYesNoIdentity,
                    StringComparison.Ordinal))
            {
                freshYesNoIdentity = snapshot.YesNoIdentity;
                stage = DadAlliancePfJoinStage.ConfirmYes;
                return Acknowledged(
                    "yesno-acknowledged",
                    "Acknowledged a fresh subgroup confirmation.",
                    snapshot);
            }

            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for the visible subgroup confirmation to become freshly ready.",
                "The visible subgroup confirmation did not become freshly ready.");
        }

        if (snapshot.PrivatePromptVisible)
        {
            if (snapshot.PrivatePromptReady)
            {
                stage = DadAlliancePfJoinStage.SubmitPasscode;
                return Acknowledged(
                    "private-prompt-acknowledged",
                    "Acknowledged a ready private passcode prompt displayed directly after subgroup selection.",
                    snapshot);
            }

            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for the directly displayed private passcode prompt to become ready.",
                "The directly displayed private passcode prompt did not become ready.");
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for a fresh subgroup confirmation.",
            "A fresh subgroup confirmation was not acknowledged.");
    }

    private DadAlliancePfJoinResult AdvanceConfirmYes(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.PrivatePromptVisible ||
            !snapshot.YesNoVisible ||
            !snapshot.YesNoReady ||
            !string.Equals(
                snapshot.YesNoIdentity,
                freshYesNoIdentity,
                StringComparison.Ordinal))
        {
            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for the acknowledged subgroup confirmation to remain the only ready prompt before Yes.",
                "The acknowledged subgroup confirmation was not the only ready prompt; DAD will retry without clicking it.");
        }

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.ConfirmYes),
            DadAlliancePfJoinStage.WaitPrivatePrompt,
            now,
            snapshot,
            "yes-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitPrivatePrompt(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.YesNoVisible &&
            snapshot.PrivatePromptVisible &&
            snapshot.PrivatePromptReady)
        {
            stage = DadAlliancePfJoinStage.SubmitPasscode;
            return Acknowledged(
                "private-prompt-acknowledged",
                "Acknowledged a ready private passcode prompt.",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for the private passcode prompt after one Yes callback.",
            "The private passcode prompt was not acknowledged after one Yes callback.");
    }

    private DadAlliancePfJoinResult AdvanceSubmitPasscode(
        DadAlliancePfJoinTarget target,
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.YesNoVisible ||
            (snapshot.PrivatePromptVisible &&
             !snapshot.PrivatePromptReady))
        {
            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for the private prompt to remain the only ready prompt before passcode submission.",
                "The private prompt was not the only ready prompt; DAD will retry without submitting the passcode.");
        }
        if (!snapshot.PrivatePromptVisible ||
            !snapshot.DetailVisible ||
            !snapshot.DetailReady)
        {
            return BeginRetry(
                now,
                snapshot,
                "The private prompt or exact detail became unavailable before passcode submission.");
        }

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SubmitPasscode,
                Passcode: target.Passcode),
            DadAlliancePfJoinStage.WaitPasscodeAcknowledged,
            now,
            snapshot,
            "passcode-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitPasscodeAcknowledged(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.PrivatePromptVisible)
        {
            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for a later snapshot to acknowledge the private passcode prompt disappearing.",
                "The private passcode prompt did not acknowledge the one passcode submission.");
        }

        if (!snapshot.DetailVisible)
        {
            currentDetailCloseDispatched = false;
            stage = DadAlliancePfJoinStage.VerifyAlliance;
            return Acknowledged(
                "passcode-acknowledged-detail-closed",
                "Acknowledged the private passcode prompt disappearing; the Party Finder detail had already closed.",
                snapshot);
        }

        stage = DadAlliancePfJoinStage.CloseJoinedDetail;
        deadlineUtc = now + ObservationTimeout;
        if (snapshot.DetailReady)
            return AdvanceCloseJoinedDetail(snapshot, now);

        return Acknowledged(
            "passcode-acknowledged-detail-pending",
            "Acknowledged the private passcode prompt disappearing; waiting for the remaining Party Finder detail to become ready before closing it.",
            snapshot);
    }

    private DadAlliancePfJoinResult AdvanceCloseJoinedDetail(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.PrivatePromptVisible)
        {
            return BeginRetry(
                now,
                snapshot,
                "The private passcode prompt reappeared before deferred detail close; DAD will not close the detail.");
        }
        if (!snapshot.DetailVisible)
        {
            currentDetailCloseDispatched = false;
            stage = DadAlliancePfJoinStage.VerifyAlliance;
            return Acknowledged(
                "passcode-acknowledged-detail-closed",
                "The Party Finder detail closed before a deferred close callback was needed.",
                snapshot);
        }
        if (!snapshot.DetailReady)
        {
            return WaitOrRetry(
                now,
                snapshot,
                "Waiting for the acknowledged remaining Party Finder detail to become ready before deferred close.",
                "The remaining Party Finder detail did not become ready after passcode acknowledgement.");
        }

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.CloseDetail),
            DadAlliancePfJoinStage.WaitJoinedDetailClosed,
            now,
            snapshot,
            "joined-detail-close-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitJoinedDetailClosed(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.DetailVisible)
        {
            currentDetailCloseDispatched = false;
            stage = DadAlliancePfJoinStage.VerifyAlliance;
            return Acknowledged(
                "joined-detail-close-acknowledged",
                "Acknowledged the one deferred Party Finder detail close.",
                snapshot);
        }

        return WaitOrRetry(
            now,
            snapshot,
            "Waiting for the one deferred Party Finder detail close to be acknowledged.",
            "The deferred Party Finder detail close was not acknowledged.");
    }

    private DadAlliancePfJoinResult AdvanceVerifyAlliance(
        DadAlliancePfJoinTarget target,
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (snapshot.ObservedAlliance != DadAllianceAssignment.None &&
            snapshot.ObservedAlliance != target.AssignedAlliance)
        {
            return BeginRetry(
                now,
                snapshot,
                $"Observed Alliance {snapshot.ObservedAlliance} instead of {target.AssignedAlliance}; guarded correction will retry.");
        }

        return WaitOrRetry(
            now,
            snapshot,
            $"Waiting to observe exact Alliance {target.AssignedAlliance} subgroup membership.",
            $"Exact Alliance {target.AssignedAlliance} subgroup membership was not observed.");
    }

    private DadAlliancePfJoinResult AdvanceCloseForRetry(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.DetailVisible)
            return CompleteRetry(snapshot);

        return Send(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.CloseDetail),
            DadAlliancePfJoinStage.WaitRetryDetailClosed,
            now,
            snapshot,
            "retry-detail-close-dispatched");
    }

    private DadAlliancePfJoinResult AdvanceWaitRetryDetailClosed(
        DadAlliancePfJoinSnapshot snapshot,
        DateTime now)
    {
        if (!snapshot.DetailVisible)
            return CompleteRetry(snapshot);

        if (now >= deadlineUtc)
        {
            var summary = $"{retryReason} Party Finder detail did not close; the worker will retry safely.";
            ResetCycle();
            return Result(
                DadAlliancePfJoinResultKind.Retry,
                "retry",
                summary,
                snapshot.ObservedAlliance,
                shouldAudit: true);
        }

        return Result(
            DadAlliancePfJoinResultKind.Waiting,
            "observation",
            "Waiting for Party Finder detail to close before retry.",
            snapshot.ObservedAlliance);
    }

    private DadAlliancePfJoinResult Send(
        DadAlliancePfJoinActionRequest request,
        DadAlliancePfJoinStage waitingStage,
        DateTime now,
        DadAlliancePfJoinSnapshot snapshot,
        string eventName)
    {
        DadAlliancePfJoinActionResult action;
        try
        {
            action = ui.Perform(request);
        }
        catch (Exception exception)
        {
            return BeginRetry(
                now,
                snapshot,
                $"{request.Action} failed before acknowledgement: {exception.Message}");
        }

        if (!action.Sent)
        {
            var error = string.IsNullOrWhiteSpace(action.Error)
                ? action.Summary
                : action.Error;
            return BeginRetry(
                now,
                snapshot,
                $"{action.Summary} {error}".Trim());
        }

        if (request.Action == DadAlliancePfJoinAction.OpenListing)
            currentDetailCloseDispatched = false;
        else if (request.Action == DadAlliancePfJoinAction.CloseDetail)
            currentDetailCloseDispatched = true;
        stage = waitingStage;
        deadlineUtc = now + ObservationTimeout;
        return Result(
            DadAlliancePfJoinResultKind.Progress,
            eventName,
            action.Summary,
            snapshot.ObservedAlliance,
            shouldAudit: true);
    }

    private DadAlliancePfJoinResult WaitOrRetry(
        DateTime now,
        DadAlliancePfJoinSnapshot snapshot,
        string waitingSummary,
        string retrySummary)
        => now >= deadlineUtc
            ? BeginRetry(now, snapshot, retrySummary)
            : Result(
                DadAlliancePfJoinResultKind.Waiting,
                "observation",
                waitingSummary,
                snapshot.ObservedAlliance);

    private DadAlliancePfJoinResult BeginRetry(
        DateTime now,
        DadAlliancePfJoinSnapshot snapshot,
        string reason)
    {
        retryReason = reason.Trim();
        if (snapshot.DetailVisible &&
            !snapshot.PrivatePromptVisible &&
            !currentDetailCloseDispatched)
        {
            stage = DadAlliancePfJoinStage.CloseForRetry;
            deadlineUtc = now + ObservationTimeout;
            return Result(
                DadAlliancePfJoinResultKind.Progress,
                "retry-close-required",
                $"{retryReason} Closing the current detail before retry.",
                snapshot.ObservedAlliance,
                shouldAudit: true);
        }

        return CompleteRetry(snapshot);
    }

    private DadAlliancePfJoinResult CompleteRetry(
        DadAlliancePfJoinSnapshot snapshot)
    {
        var summary = retryReason;
        ResetCycle();
        return Result(
            DadAlliancePfJoinResultKind.Retry,
            "retry",
            summary,
            snapshot.ObservedAlliance,
            shouldAudit: true);
    }

    private void ResetCycle()
    {
        retryCycle++;
        stage = DadAlliancePfJoinStage.EnsureWindow;
        deadlineUtc = DateTime.MinValue;
        listingCursor = 0;
        listingCount = 0;
        freshYesNoIdentity = string.Empty;
        retryReason = string.Empty;
        currentDetailCloseDispatched = false;
    }

    private DadAlliancePfJoinResult Acknowledged(
        string eventName,
        string summary,
        DadAlliancePfJoinSnapshot snapshot)
        => Result(
            DadAlliancePfJoinResultKind.Progress,
            eventName,
            summary,
            snapshot.ObservedAlliance,
            shouldAudit: true);

    private DadAlliancePfJoinResult Result(
        DadAlliancePfJoinResultKind kind,
        string eventName,
        string summary,
        DadAllianceAssignment observedAlliance,
        bool shouldAudit = false)
        => new(
            kind,
            stage,
            eventName,
            summary,
            retryCycle,
            listingCursor,
            observedAlliance,
            shouldAudit);

    private static bool HasPrivateMain(DadAlliancePfJoinSnapshot snapshot)
        => snapshot.MainVisible &&
           snapshot.MainReady &&
           snapshot.SearchAreaTab ==
           DadAlliancePartyFinderJoinCallbacks.PrivateSearchAreaTab;

    private static bool HasPrivateRaidsMain(DadAlliancePfJoinSnapshot snapshot)
        => HasPrivateMain(snapshot) &&
           snapshot.CategoryTab ==
           DadAlliancePartyFinderJoinCallbacks.RaidsCategoryIndex;

    private static bool IsExactListing(
        DadAlliancePfJoinTarget target,
        DadAlliancePfJoinSnapshot snapshot)
        => string.Equals(
               target.LeaderName.Trim(),
               snapshot.DetailLeaderName.Trim(),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               target.LeaderWorld.Trim(),
               snapshot.DetailLeaderWorld.Trim(),
               StringComparison.OrdinalIgnoreCase) &&
           snapshot.DetailDutyId == LabyrinthDutyId &&
           snapshot.DetailPrivate &&
           snapshot.DetailAlliance &&
           snapshot.DetailPartyCount == 3;

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
