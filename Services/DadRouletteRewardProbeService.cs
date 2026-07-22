using dad.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace dad.Services;

/// <summary>
/// Reads the frozen roulette completion flag exclusively on the framework thread. The transport
/// request remains poll-based: the first exact request starts an operation and later identical
/// requests receive Pending until two stable native observations produce a terminal result.
/// </summary>
public sealed unsafe class DadRouletteRewardProbeService : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(2);

    private readonly IFramework framework;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private ProbeSession? session;
    private bool disposed;

    public DadRouletteRewardProbeService(
        IFramework framework,
        DadPresenceService presenceService,
        IPluginLog log)
    {
        this.framework = framework;
        this.presenceService = presenceService;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public DadRouletteRewardProbeResultDto Handle(DadRouletteRewardProbeRequestDto request)
    {
        var now = DateTime.UtcNow;
        if (!DadRouletteRewardProbeIdentityRules.IsValid(request) ||
            request.RequestedAtUtc > now ||
            now - request.RequestedAtUtc > MaximumRequestAge)
        {
            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                DadRouletteRewardProbeOutcome.Unknown,
                "Roulette reward probe rejected an incomplete or stale request identity.",
                now);
        }

        if (request.Operation == DadRouletteRewardProbeOperation.Cancel)
        {
            if (session != null && SameCoreIdentity(session.Request, request))
            {
                Complete(
                    session,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    "Roulette reward probe cancelled by scheduler.",
                    now);
            }

            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                DadRouletteRewardProbeOutcome.Unknown,
                "Roulette reward probe cancellation accepted.",
                now);
        }

        if (session == null || session.TerminalResult != null && !SameCoreIdentity(session.Request, request))
        {
            session = new ProbeSession(request.Clone(), now);
            log.Information(
                "[dad] Started direct Daily Roulette reward probe operation={OperationId} schedule={ScheduleId}/{ScheduleRunId}/{ScheduleEntryId} slot={SlotId} route={WorkerSessionId} character={CharacterKey} roulette={RouletteId}.",
                request.OperationId,
                request.ScheduleId,
                request.ScheduleRunId,
                request.ScheduleEntryId,
                request.SlotId,
                request.RouteWorkerSessionId,
                request.CharacterKey,
                request.RouletteId);
        }
        else if (!SameCoreIdentity(session.Request, request) || session.Request.Operation != request.Operation)
        {
            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                DadRouletteRewardProbeOutcome.Unknown,
                "Another roulette reward probe owns the local native read operation.",
                now);
        }

        return BuildResult(session, now);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed || session == null || session.TerminalResult != null)
            return;

        var now = DateTime.UtcNow;
        try
        {
            if (now >= session.DeadlineUtc)
            {
                Complete(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Roulette reward probe timed out.", now);
                return;
            }

            if (!TryValidateLocalIdentity(session.Request, out var identityFailure))
            {
                Complete(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, identityFailure, now);
                return;
            }

            var instanceContent = InstanceContent.Instance();
            if (instanceContent == null)
            {
                Complete(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "InstanceContent reward state is unavailable.", now);
                return;
            }

            var observation = new DadDirectRouletteRewardObservation(
                Plugin.PlayerState.ContentId,
                session.Request.RouletteId,
                instanceContent->IsRouletteComplete(checked((byte)session.Request.RouletteId)),
                now);
            var status = session.ObservationGate.Observe(
                observation,
                session.Request.CharacterContentId,
                session.Request.RouletteId,
                out var reason);
            session.Summary = reason;
            if (status == DadRouletteRewardObservationStatus.Invalid)
            {
                Complete(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, reason, now);
            }
            else if (status == DadRouletteRewardObservationStatus.Received)
            {
                Complete(session, DadRouletteRewardProbeOutcome.Received, 1, 1, "Two stable direct native reads report the frozen roulette reward received.", now);
            }
            else if (status == DadRouletteRewardObservationStatus.NotReceived)
            {
                Complete(session, DadRouletteRewardProbeOutcome.NotReceived, 0, 1, "Two stable direct native reads report the frozen roulette reward not received.", now);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Direct Daily Roulette reward probe failed safely for {OperationId}.", session.Request.OperationId);
            Complete(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, $"Roulette reward probe failed safely: {ex.Message}", now);
        }
    }

    private bool TryValidateLocalIdentity(DadRouletteRewardProbeRequestDto request, out string failure)
    {
        var live = presenceService.BuildLiveSafetySnapshot();
        if (!string.Equals(live.WorkerSessionId.Value, request.RouteWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ||
            !DadRosterIdentity.SameAccount(live.ManagedAccountKey, request.AccountKey) ||
            !string.Equals(live.ActiveCharacterKey.Value, request.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(live.Character.CharacterKey, request.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            live.Character.ContentId != request.CharacterContentId ||
            !live.IsAvailable ||
            !live.WorldReadyStable)
        {
            failure = "The local route is not world-ready on the exact requested account, character, and Content ID.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private void Complete(
        ProbeSession active,
        DadRouletteRewardProbeOutcome outcome,
        int received,
        int maximum,
        string summary,
        DateTime now)
    {
        if (active.TerminalResult != null)
            return;

        active.Summary = summary;
        active.TerminalResult = DadRouletteRewardProbeResultDto.FromRequest(
            active.Request,
            outcome,
            summary,
            now,
            received,
            maximum,
            dutyFinderOpenedByDad: false);
        log.Information(
            "[dad] Direct Daily Roulette reward probe operation={OperationId} slot={SlotId} outcome={Outcome} rewards={Received}/{Maximum}: {Summary}",
            active.Request.OperationId,
            active.Request.SlotId,
            outcome,
            received,
            maximum,
            summary);
    }

    private static DadRouletteRewardProbeResultDto BuildResult(ProbeSession active, DateTime now)
        => active.TerminalResult?.Clone() ?? DadRouletteRewardProbeResultDto.FromRequest(
            active.Request,
            DadRouletteRewardProbeOutcome.Pending,
            string.IsNullOrWhiteSpace(active.Summary) ? "Roulette reward probe is pending." : active.Summary,
            now,
            dutyFinderOpenedByDad: false);

    private static bool SameCoreIdentity(
        DadRouletteRewardProbeRequestDto left,
        DadRouletteRewardProbeRequestDto right)
        => string.Equals(left.OperationId, right.OperationId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.SchedulerRunId, right.SchedulerRunId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ScheduleId, right.ScheduleId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ScheduleRunId, right.ScheduleRunId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ScheduleEntryId, right.ScheduleEntryId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.SlotId, right.SlotId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.RouteWorkerSessionId.Value, right.RouteWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(left.AccountKey, right.AccountKey) &&
           string.Equals(left.CharacterKey.Value, right.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
           left.CharacterContentId == right.CharacterContentId &&
           left.RouletteId == right.RouletteId &&
           string.Equals(left.RouletteKey, right.RouletteKey, StringComparison.OrdinalIgnoreCase) &&
           left.RequestedAtUtc == right.RequestedAtUtc;

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        session = null;
    }

    private sealed class ProbeSession
    {
        public ProbeSession(DadRouletteRewardProbeRequestDto request, DateTime now)
        {
            Request = request;
            DeadlineUtc = now + ProbeTimeout;
        }

        public DadRouletteRewardProbeRequestDto Request { get; }
        public DateTime DeadlineUtc { get; }
        public string Summary { get; set; } = "Waiting for two stable direct native roulette reward reads.";
        public DadDirectRouletteRewardObservationGate ObservationGate { get; } = new();
        public DadRouletteRewardProbeResultDto? TerminalResult { get; set; }
    }
}
