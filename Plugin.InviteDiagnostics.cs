using dad.Models;
using dad.Services;

namespace dad;

public sealed partial class Plugin
{
    private string inviteTestFormationId = string.Empty;

    private void RunInviteDiagnosticsCommand(string argument)
    {
        var diagnostics = PartyInviteGateway.Diagnostics;
        var command = argument.Trim().ToLowerInvariant();
        if (command == "stop")
        {
            StopInviteDiagnostics("Stopped by operator.");
            PrintStatus(diagnostics.Summary);
            return;
        }
        if (command == "status")
        {
            PrintStatus(diagnostics.Summary + " Use /dad report for the bounded invite timeline.");
            return;
        }
        if (command is not ("mixed" or "cross" or "serial" or "reverse"))
        {
            PrintStatus("Invite comparison: /dad test invites mixed|cross|serial|reverse|status|stop. Select an online saved crew first; /dad debug on is required on all clients. No dungeon will be queued.");
            return;
        }
        if (!Configuration.DebugUiEnabled)
        {
            PrintStatus("Enable /dad debug on before starting an invite comparison.");
            return;
        }
        if (diagnostics.Active)
        {
            PrintStatus("An invite comparison is already active. Use /dad test invites stop first.");
            return;
        }
        var pool = BuildPlannerPool();
        if (!TryBuildCrewFormationSelection(pool, out var selection, out var blocker))
        {
            PrintStatus(blocker);
            return;
        }
        blocker = FirstNonEmpty(BuildCrewSelectionBlocker(selection),
            BuildCrewFormationOperationalBlocker(selection.Classification.Mode));
        if (string.IsNullOrWhiteSpace(blocker) && selection.Classification.Mode != DadCrewFormationMode.RegularParty)
            blocker = "Invite comparison supports regular LAN crews only, not alliance PF or registered islands.";
        var request = selection.RequestPreview.Request;
        var roster = request?.Orchestration.RequiredRosterCharacters ?? [];
        if (string.IsNullOrWhiteSpace(blocker) && !selection.RequestPreview.CanStart)
            blocker = FirstNonEmpty(selection.RequestPreview.BlockedReason,
                selection.RequestPreview.ReadinessSummary, "The selected crew is not live-ready yet.");
        if (string.IsNullOrWhiteSpace(blocker) &&
            (roster.Count is < 2 or > 8 ||
             roster.Any(row => row.ContentId == 0 || row.AccountKey.IsEmpty || row.CharacterKey.IsEmpty || !string.IsNullOrWhiteSpace(row.SharedIdentityToken))))
            blocker = "Invite comparison requires an exact, already-online regular crew with 2–8 members.";
        if (string.IsNullOrWhiteSpace(blocker) &&
            (roster[0].ContentId != PlayerState.ContentId || !PartyInviteGateway.IsInviteTestSafe() ||
             PartyInviteGateway.ReadAuthoritativePartyMembers().Any(member => member.ContentId != PlayerState.ContentId)))
            blocker = "Start on the selected Slot1 coordinator, solo, outside a duty/queue and with stable world state.";
        if (string.IsNullOrWhiteSpace(blocker))
        {
            foreach (var row in roster)
            {
                var matches = pool.Characters.Where(character => character.ContentId == row.ContentId &&
                    string.Equals(character.AccountId, row.AccountKey.Value, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(character.CharacterKey, row.CharacterKey.Value, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1 || !matches[0].IsLiveConnected ||
                    matches[0].PartyRosterCount is > 1 || matches[0].VisiblePartyCount is > 1)
                {
                    blocker = "Every exact roster member must already be online, ready and solo. The comparison does not wake or relog clients.";
                    break;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(blocker) || request == null)
        {
            PrintStatus(string.IsNullOrWhiteSpace(blocker) ? "No exact formation request is available." : blocker);
            return;
        }

        // Runtime copies only: do not change jobs, execute load commands or save preset edits for a timing test.
        DadInviteDiagnostics.PrepareRuntimeCrew(selection.EffectiveGroup,
            selection.RequestPreview, selection.AlliancePreview);
        var mode = command switch
        {
            "cross" => DadInviteTestMode.Cross,
            "serial" => DadInviteTestMode.Serial,
            "reverse" => DadInviteTestMode.Reverse,
            _ => DadInviteTestMode.Mixed,
        };
        diagnostics.Start(request.RequestId, mode, roster.Skip(1).Select(row => row.ContentId), DadClock.UtcNow);
        try
        {
            var formation = SchedulerService.StartCrewFormation(selection.SourceGroup, selection.EffectiveGroup,
                selection.RequestPreview, selection.AlliancePreview, selection.Classification);
            inviteTestFormationId = formation.RunId;
            if (!formation.IsActive || !string.Equals(formation.RequestId, request.RequestId, StringComparison.Ordinal))
                StopInviteDiagnostics("Formation could not start: " + formation.Summary);
            PrintStatus(diagnostics.Summary);
            InvalidatePlannerPreviewCache("Invite comparison started");
        }
        catch
        {
            StopInviteDiagnostics("Formation startup threw; comparison revoked.");
            throw;
        }
    }

    private void UpdateInviteDiagnostics()
    {
        var diagnostics = PartyInviteGateway.Diagnostics;
        if (!diagnostics.Active && string.IsNullOrEmpty(inviteTestFormationId)) return;
        var formation = SchedulerService.GetCrewFormationStatus();
        if (!string.Equals(formation.RunId, inviteTestFormationId, StringComparison.Ordinal) ||
            !string.Equals(formation.RequestId, diagnostics.RunId, StringComparison.Ordinal))
        {
            diagnostics.Finish(DadClock.UtcNow, "Owned formation ended or was replaced; policy revoked.");
            inviteTestFormationId = string.Empty;
            return;
        }
        if (!diagnostics.Active)
        {
            if (formation.Phase != DadCrewFormationPhase.RegularGroupReady)
                StopInviteDiagnostics("Comparison policy ended; cancelling its exact formation.");
            return;
        }
        if (!Configuration.DebugUiEnabled || !Configuration.PluginEnabled || DadClock.UtcNow >= diagnostics.DeadlineUtc)
        {
            StopInviteDiagnostics("Stopped: debug/plugin disabled or 120-second deadline reached.");
            PrintStatus(diagnostics.Summary);
            return;
        }
        diagnostics.ObserveMembers(PartyInviteGateway.ReadAuthoritativePartyMembers().Select(member => member.ContentId).ToArray(), DadClock.UtcNow);
        if (formation.Phase == DadCrewFormationPhase.RegularGroupReady)
        {
            diagnostics.Finish(DadClock.UtcNow, "Exact party formed; held at GroupReady, no duty queued. Use /dad test invites stop before another comparison.");
            PrintStatus(diagnostics.Summary);
        }
        else if (!formation.IsActive)
        {
            diagnostics.Finish(DadClock.UtcNow, "Formation ended: " + formation.Phase);
            inviteTestFormationId = string.Empty;
            PrintStatus(diagnostics.Summary);
        }
    }

    private void StopInviteDiagnostics(string reason)
    {
        var diagnostics = PartyInviteGateway.Diagnostics;
        diagnostics.Finish(DadClock.UtcNow, reason);
        var formation = SchedulerService.GetCrewFormationStatus();
        if (!string.IsNullOrEmpty(inviteTestFormationId) &&
            string.Equals(formation.RunId, inviteTestFormationId, StringComparison.Ordinal) &&
            string.Equals(formation.RequestId, diagnostics.RunId, StringComparison.Ordinal))
        {
            if (string.Equals(RunCoordinatorService.GetLocalResult().RequestId, diagnostics.RunId, StringComparison.Ordinal))
                RunCoordinatorService.CancelActiveRun();
            SchedulerService.CancelCrewFormation(reason);
        }
        inviteTestFormationId = string.Empty;
    }
}
