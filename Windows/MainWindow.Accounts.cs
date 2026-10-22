using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using dad.Models;

namespace dad.Windows;

public sealed partial class MainWindow
{
    private readonly DadRosterEnabledDrafts rosterEnabledDrafts = new();

    private void DrawAccountSummary(DadCharacterPool pool, bool showDetails)
    {
        var legacyParticipantCardsId = ImGui.GetID("dad-home-participant-cards");
        var summary = DadAccountSummaryProjection.Build(plugin.PresenceService.BuildSnapshotCopy(),
            pool.PeerTransport.KnownParticipants, DateTime.UtcNow);
        DadUi.Section("Connected accounts");
        using (UiText.Font(UiFontRole.Caption))
            MaterialText.TextDisabled(UiText.F("{0} account(s)", summary.Accounts.Count));

        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        using var geometry = new MaterialStyleScope();
        var scale = MaterialTheme.Metrics.Scale;
        geometry.Style(ImGuiStyleVar.CellPadding, new Vector2(8, DadPresentation.Compact ? 4 : 6) * scale);
        var stateWidth = Math.Max(112 * scale, new[] { "State", "Multiple sessions", "AutoRetainer busy" }
            .Max(caption => MaterialText.Measure(UiText.T(caption)).X) + 16 * scale);
        var eligibilityWidth = Math.Max(110 * scale, new[] { "Eligibility", "Eligible", "Post-AR ready", "Waiting", "Review" }
            .Max(caption => MaterialText.Measure(UiText.T(caption)).X) + 16 * scale);
        var detailsWidth = MaterialText.Measure(UiText.T("Details")).X + ImGui.GetStyle().FramePadding.X * 2;
        var summaryMinimum = Math.Max(112 * scale, MaterialText.Measure(UiText.T("Account")).X + 16 * scale)
            + Math.Max(180 * scale, MaterialText.Measure(UiText.T("Current character")).X + 16 * scale)
            + stateWidth + eligibilityWidth + (showDetails ? detailsWidth + 16 * scale : 0);
        var wide = ImGui.GetContentRegionAvail().X >= Math.Max(640 * scale, summaryMinimum);
        if (summary.Accounts.Count > 0 && ImGui.BeginTable("dad-account-summary", wide ? (showDetails ? 5 : 4) : 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            if (wide)
            {
                ImGui.TableSetupColumn(UiText.T("Account"), ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn(UiText.T("Current character"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
                ImGui.TableSetupColumn(UiText.T("State"), ImGuiTableColumnFlags.WidthFixed, stateWidth);
                ImGui.TableSetupColumn(UiText.T("Eligibility"), ImGuiTableColumnFlags.WidthFixed, eligibilityWidth);
                if (showDetails)
                    ImGui.TableSetupColumn(UiText.T("Action"), ImGuiTableColumnFlags.WidthFixed, detailsWidth);
                UiGui.TableHeadersRow();
            }
            foreach (var row in summary.Accounts)
            {
                var participant = row.Participant;
                ImGui.PushID(row.AccountKey.Value);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                using (UiText.Font(UiFontRole.BodyStrong))
                    MaterialText.SingleLine(FormatOperatorAccountLabel(participant.ManagedAccountAlias, string.Empty));
                using (UiText.Font(UiFontRole.Caption))
                    MaterialText.SingleLine(UiText.T(participant.WorkerRole == DadWorkerRole.ServerDad || participant.IsAuthority
                        ? "Coordinator" : participant.WorkerRole == DadWorkerRole.ClientDad ? "Client" : "Unknown"));
                ImGui.TableNextColumn();
                MaterialText.SingleLine(row.HasMultipleLiveSessions ? UiText.T("Multiple sessions")
                    : FormatOperatorCharacterKey(participant.ActiveCharacterKey.Value, "(unknown)"));
                if (!row.HasMultipleLiveSessions && (participant.Character.CurrentJobId.HasValue || participant.Character.CurrentLevel.HasValue))
                {
                    using var caption = UiText.Font(UiFontRole.Caption);
                    MaterialText.SingleLine(FormatJobLevelEntry(new JobLevelEntry(participant.Character.CurrentJobId,
                        participant.Character.CurrentJobAbbrev, participant.Character.CurrentLevel)));
                }
                ImGui.TableNextColumn();
                var stale = participant.State == DadParticipantState.Stale || IsParticipantStale(participant);
                var state = row.HasMultipleLiveSessions ? "Multiple sessions" : stale ? "Stale"
                    : participant.AutoRetainerBusy ? "AutoRetainer busy" : participant.State.ToString();
                MaterialText.SingleLine(UiText.T(state));
                ImGui.TableNextColumn();
                var eligibility = row.HasMultipleLiveSessions ? "Review" : participant.IsEligibleForRun ? "Eligible"
                    : participant.PostArReady ? "Post-AR ready" : "Waiting";
                ImGui.PushStyleColor(ImGuiCol.Text, row.HasMultipleLiveSessions || !participant.IsEligibleForRun ? DadUi.Warning : DadUi.Success);
                MaterialText.SingleLine(UiText.T(eligibility));
                ImGui.PopStyleColor();
                if (showDetails)
                {
                    if (wide) ImGui.TableNextColumn();
                    else DadUi.SameLineIfFits(DadUi.ButtonWidth("Details"));
                    ImGuiP.PushOverrideID(legacyParticipantCardsId);
                    ImGui.PushID("dad-home-participant-" + participant.WorkerSessionId.Value);
                    if (UiGui.SmallButton("Details##dad-home-participant-details"))
                        plugin.OpenMainTab(DadMainWindowTab.Multiplayer);
                    ImGui.PopID();
                    ImGui.PopID();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        if (summary.Unidentified.Count > 0)
        {
            var coordinators = summary.Unidentified.Count(static participant => participant.WorkerRole == DadWorkerRole.ServerDad || participant.IsAuthority);
            using var caption = UiText.Font(UiFontRole.Caption);
            MaterialText.TextDisabled(UiText.F("{0} connection(s) awaiting account identity; {1} coordinator(s).", summary.Unidentified.Count, coordinators));
        }
    }

    private DadRosterProfileTarget ResolveRosterProfile(DadRosterCharacter character)
        => DadRosterEnabledDrafts.Resolve(character, plugin.ProfileDirectoryService.GetCatalogs(), plugin.PresenceService.WorkerSessionId);

    private DadRosterProfileTarget ResolveRosterDraft(DadRosterEnabledDraft draft)
        => ResolveRosterProfile(new DadRosterCharacter
        {
            AccountKey = draft.Target.Account!.AccountKey,
            CharacterKey = draft.Target.Character!.CharacterKey,
        });

    private void SaveRosterEnabledDrafts(IEnumerable<DadRosterEnabledDraft> drafts)
        => rosterEnabledDrafts.Save(drafts.ToList(), ResolveRosterDraft,
            (request, completed) => plugin.ProfileDirectoryService.UpdateProfile(request, completed));

    private float RosterEnabledColumnWidth()
    {
        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        using var lineHeight = MaterialText.PushLineHeight(UiText.T("Enabled"), UiText.T("Disabled"));
        var label = new[] { MaterialText.Measure(UiText.T("Enabled")).X, MaterialText.Measure(UiText.T("Disabled")).X,
            ImGui.CalcTextSize("Enabled").X, ImGui.CalcTextSize("Disabled").X }.Max();
        return ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + label
            + MaterialText.Measure(UiText.T("Save")).X + MaterialText.Measure(UiText.T("Cancel")).X
            + 4 * ImGui.GetStyle().FramePadding.X + 16 * MaterialTheme.Metrics.Scale;
    }

    private float RosterTableMinimumWidth(bool account, bool provenance)
        => RosterColumns(account, provenance).Sum(static column => column.Width);

    private float RosterEnabledRowHeight()
    {
        float controlHeight;
        using (MaterialControls.Push(MaterialControlContext.Toolbar))
        using (MaterialText.PushLineHeight(UiText.T("Enabled"), UiText.T("Disabled"), UiText.T("Save"), UiText.T("Cancel")))
            controlHeight = ImGui.GetFrameHeight();
        using var caption = UiText.Font(UiFontRole.Caption);
        var statusHeight = MaterialText.Measure(UiText.T("Awaiting acknowledgement")).Y;
        return MathF.Ceiling(controlHeight + statusHeight + 2 * MaterialTheme.Metrics.Scale + 2 * ImGui.GetStyle().CellPadding.Y);
    }

    private IReadOnlyList<(string Heading, float Width)> RosterColumns(bool account, bool provenance)
    {
        var scale = MaterialTheme.Metrics.Scale;
        float Width(string heading, float minimum) => Math.Max(minimum * scale, MaterialText.Measure(UiText.T(heading)).X + 16 * scale);
        var columns = new List<(string Heading, float Width)> { ("Sel", Width("Sel", 34)) };
        if (account) columns.Add(("Account", Width("Account", 110)));
        columns.Add(("Character", Width("Character", 160)));
        columns.Add(("Enabled", Math.Max(Width("Enabled", 0), RosterEnabledColumnWidth())));
        if (provenance) columns.Add(("ContentId", Width("ContentId", 110)));
        columns.Add(("World/DC", Width("World/DC", 120)));
        if (provenance) columns.Add(("Snapshot age", Width("Snapshot age", 170)));
        columns.Add(("Job/Lvl", Width("Job/Lvl", 100)));
        columns.Add(("State", Width("State", 100)));
        if (provenance) columns.Add(("Source", Width("Source", 150)));
        columns.Add(("Blockers", Width("Blockers", 140)));
        columns.Add(("Actions", Math.Max(Width("Actions", 0), (DadUi.ButtonWidth("Activate") + DadUi.ButtonWidth("More...")) * scale + 12 * scale)));
        return columns;
    }

    private void DrawRosterEnabledCell(DadRosterCharacter character)
    {
        var target = ResolveRosterProfile(character);
        var draft = rosterEnabledDrafts.Get(target);
        var locked = rosterEnabledDrafts.IsAccountSaving(target);
        ImGui.PushID("dad-roster-enabled-" + BuildRosterSelectionKey(character));
        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        using var spacing = new MaterialStyleScope();
        spacing.Style(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2) * MaterialTheme.Metrics.Scale);
        if (target.Character == null)
        {
            MaterialText.SingleLine(UiText.T("Read-only"));
            if (ImGui.IsItemHovered()) UiGui.SetTooltip(target.Reason);
            ImGui.PopID();
            return;
        }
        var enabled = draft?.Enabled ?? target.Character.Profile.Enabled;
        ImGui.BeginDisabled(!target.Writable || locked);
        if (UiGui.Checkbox($"{(enabled ? "Enabled" : "Disabled")}###enabled", ref enabled))
            rosterEnabledDrafts.Stage(target, enabled);
        ImGui.EndDisabled();
        draft = rosterEnabledDrafts.Get(target);
        if (draft != null)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(!target.Writable || locked);
            if (UiGui.SmallButton("Save##enabled-save"))
                SaveRosterEnabledDrafts([draft]);
            ImGui.SameLine();
            if (UiGui.SmallButton("Cancel##enabled-cancel"))
                rosterEnabledDrafts.Cancel(target);
            ImGui.EndDisabled();
        }
        var status = rosterEnabledDrafts.GetStatus(target);
        if (string.IsNullOrWhiteSpace(status))
            status = !target.Writable ? target.Reason : rosterEnabledDrafts.Get(target) != null ? "Pending changes"
                : locked ? "Account save in progress" : string.Empty;
        if (!string.IsNullOrWhiteSpace(status))
        {
            using var caption = UiText.Font(UiFontRole.Caption);
            MaterialText.SingleLine(UiText.T(status));
            if (ImGui.IsItemHovered()) UiGui.SetTooltip(status);
        }
        ImGui.PopID();
    }

    private void DrawRosterSelectFiltered(IReadOnlyList<DadRosterCharacter> filtered)
    {
        UiGui.TextDisabled("Characters are enabled by default. Edit stored profiles here without logging into each character.");
        var selected = filtered.Count > 0 && filtered.All(character => rosterSelectedRows.Contains(BuildRosterSelectionKey(character)));
        ImGui.BeginDisabled(filtered.Count == 0);
        if (UiGui.Checkbox("Select filtered", ref selected))
            foreach (var row in filtered)
                if (selected) rosterSelectedRows.Add(BuildRosterSelectionKey(row));
                else rosterSelectedRows.Remove(BuildRosterSelectionKey(row));
        ImGui.EndDisabled();
    }

    private void DrawRosterToolbar(IReadOnlyList<DadRosterCharacter> filtered)
    {
        var selected = filtered.Where(character => rosterSelectedRows.Contains(BuildRosterSelectionKey(character))).ToList();
        var targets = selected.Select(ResolveRosterProfile).ToList();
        var writable = targets.Where(target => target.Writable && !rosterEnabledDrafts.IsAccountSaving(target)).ToList();
        using var font = UiText.Font(UiFontRole.Caption);
        using var controls = MaterialControls.Push(MaterialControlContext.Dense);
        using var spacing = new MaterialStyleScope();
        spacing.Style(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4) * MaterialTheme.Metrics.Scale);
        spacing.Style(ImGuiStyleVar.FramePadding, new Vector2(5, 3) * MaterialTheme.Metrics.Scale);
        UiGui.TextUnformatted(UiText.F("{0} selected", selected.Count));
        if (targets.Any(static target => !target.Writable))
        {
            DadUi.SameLineIfFits(MaterialText.Measure(UiText.F("{0} read-only", targets.Count(static target => !target.Writable))).X / MaterialTheme.Metrics.Scale);
            UiGui.TextColored(DadUi.Warning, UiText.F("{0} read-only", targets.Count(static target => !target.Writable)));
        }
        bool Button(string id, string caption, bool disabled)
        {
            DadUi.SameLineIfFits(DadUi.ButtonWidth(caption));
            ImGui.BeginDisabled(disabled);
            var clicked = UiGui.Button(id, new Vector2(0, (DadPresentation.Compact ? 26 : 28) * MaterialTheme.Metrics.Scale), display: UiText.T(caption));
            ImGui.EndDisabled();
            return clicked;
        }
        if (Button("Enable selected", "Enable selected", writable.Count == 0))
            foreach (var target in writable) rosterEnabledDrafts.Stage(target, true);
        if (Button("Disable selected", "Disable selected", writable.Count == 0))
            foreach (var target in writable) rosterEnabledDrafts.Stage(target, false);
        if (Button("Activate selected", "Activate", selected.Count == 0)) SetRosterVisibility(selected, DadRosterVisibility.Active);
        if (Button("Hide selected", "Hide", selected.Count == 0)) SetRosterVisibility(selected, DadRosterVisibility.Hidden);
        if (Button("Ignore selected", "Ignore", selected.Count == 0)) SetRosterVisibility(selected, DadRosterVisibility.Ignored);
        if (Button("Mark update selected", "Mark update", selected.Count == 0)) SetRosterVisibility(selected, DadRosterVisibility.NeedsUpdate);
        if (Button("Queue selected update", "Queue update", selected.Count == 0)) QueueRosterUpdate(selected, dryRun: false);
        if (Button("Dry-run selected update", "Dry-run update", selected.Count == 0)) QueueRosterUpdate(selected, dryRun: true);
        if (Button("Clear selection", "Clear selection", rosterSelectedRows.Count == 0)) rosterSelectedRows.Clear();

        var visiblePending = targets.Select(rosterEnabledDrafts.Get).OfType<DadRosterEnabledDraft>().Distinct().ToList();
        var pending = writable.Select(rosterEnabledDrafts.Get).OfType<DadRosterEnabledDraft>().Distinct().ToList();
        if (visiblePending.Count > 0)
        {
            UiGui.TextUnformatted(UiText.F("{0} pending change(s)", visiblePending.Count));
            if (Button("Save selected", "Save selected", pending.Count == 0)) SaveRosterEnabledDrafts(pending);
            if (Button("Cancel selected", "Cancel selected", pending.Count == 0))
                foreach (var draft in pending) rosterEnabledDrafts.Cancel(draft.Target);
        }
    }
}
