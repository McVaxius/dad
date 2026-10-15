using dad.Models;
using dad.Services;

namespace dad;

public sealed partial class Plugin
{
    private DadGatherPlanProbe? gatherPreviewProbe;
    private DateTime nextGatherPreviewReadUtc;
    internal DadGatherPartyPreview? GatherPreview { get; private set; }
    internal string GatherPreviewStatus { get; private set; } = "Refresh carried stock to preview fixed allocations.";
    private string gatherPreviewCrew = string.Empty;
    private DadGatherPartyTask? gatherPreviewTask;

    internal bool IsGatherPreviewFor(DadGatherPartyTask task, IReadOnlyList<DadPresetCharacterSlot> selected)
        => ReferenceEquals(gatherPreviewTask, task) && gatherPreviewCrew == GatherCrewIdentity(selected);

    private static string GatherCrewIdentity(IReadOnlyList<DadPresetCharacterSlot> selected)
        => string.Join("|", selected.Select(slot => $"{slot.SlotId}:{slot.RequiredAccountKey.Value}:{slot.CharacterKey}:{slot.ContentId}"));

    internal void ClearGatherPreview()
    {
        gatherPreviewProbe = null;
        gatherPreviewTask = null;
        GatherPreview = null;
        GatherPreviewStatus = "Refresh carried stock to preview fixed allocations.";
    }

    internal void RefreshGatherPreview(DadGatherPartyTask task, IReadOnlyList<DadPresetCharacterSlot> selected)
    {
        ClearGatherPreview();
        gatherPreviewCrew = GatherCrewIdentity(selected);
        gatherPreviewTask = task;
        var known = TransportService.CurrentTransport.KnownParticipants.Prepend(PresenceService.BuildLiveSafetySnapshot())
            .DistinctBy(participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase).ToArray();
        var workers = new List<DadParticipantSnapshot>();
        foreach (var slot in selected)
        {
            var matches = known.Where(participant =>
                string.Equals(participant.ActiveCharacterKey.Value, slot.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                (slot.RequiredAccountKey.IsEmpty || DadRosterIdentity.SameAccount(slot.RequiredAccountKey, participant.ManagedAccountKey)) &&
                (!slot.ContentId.HasValue || slot.ContentId == participant.Character.ContentId)).ToArray();
            if (matches.Length != 1 || !string.IsNullOrWhiteSpace(slot.SharedIdentityToken))
            {
                GatherPreviewStatus = "Select directly connected DAD clients to read gathering stock.";
                return;
            }
            var worker = matches[0].Clone();
            worker.AssignedSlotId = slot.SlotId;
            workers.Add(worker);
        }
        gatherPreviewProbe = new(task, workers, Guid.NewGuid().ToString("N"), DadClock.UtcNow);
        nextGatherPreviewReadUtc = DateTime.MinValue;
        GatherPreviewStatus = gatherPreviewProbe.Pending ? "Reading selected clients' carried stock and gathering eligibility." : gatherPreviewProbe.Error;
    }

    internal void RecalculateGatherPreview(DadGatherPartyTask task)
    {
        if (gatherPreviewProbe?.Preview == null) return;
        GatherPreview = DadGatherPartyPlanningRules.Preview(task, gatherPreviewProbe.Stocks);
        GatherPreviewStatus = GatherPreview.CanStart ? "Fixed allocations are ready. Start rechecks current stock." : string.Join(" | ", GatherPreview.Blockers);
    }

    private void UpdateGatherPreview()
    {
        if (gatherPreviewProbe == null || !gatherPreviewProbe.Pending || DadClock.UtcNow < nextGatherPreviewReadUtc) return;
        nextGatherPreviewReadUtc = DadClock.UtcNow.AddSeconds(1);
        gatherPreviewProbe.Update(TransportService.SendGatherStockQuery, DadClock.UtcNow,
            TimeSpan.FromSeconds(Math.Max(30, Configuration.ParticipantReadyTimeoutSeconds)));
        if (gatherPreviewProbe.Pending) return;
        GatherPreview = gatherPreviewProbe.Preview == null ? null : DadGatherPartyPlanningRules.Preview(
            gatherPreviewTask!, gatherPreviewProbe.Stocks);
        GatherPreviewStatus = !string.IsNullOrEmpty(gatherPreviewProbe.Error) ? gatherPreviewProbe.Error :
            GatherPreview?.CanStart == true ? "Fixed allocations are ready. Start rechecks current stock." :
            string.Join(" | ", GatherPreview?.Blockers ?? []);
    }
}
