using dad.Models;

namespace dad.Services;

// One explicit preview/dispatch/final-stock query. Existing transport owns its request
// lifecycle; completed responses are consumed once and no new attempt is created here.
internal sealed class DadGatherPlanProbe
{
    private readonly DadGatherPartyTask task;
    private readonly List<(DadParticipantSnapshot Participant, DadGatherStockQuery Query)> queries;
    private readonly Dictionary<string, DadGatherWorkerStock> stocks = new(StringComparer.Ordinal);
    private readonly DateTime startedUtc;
    public DadGatherPartyPreview? Preview { get; private set; }
    public string Error { get; private set; } = string.Empty;
    public bool Pending => Preview == null && string.IsNullOrEmpty(Error);
    public IReadOnlyList<DadGatherWorkerStock> Stocks => queries.Where(entry => stocks.ContainsKey(entry.Query.RequestId))
        .Select(entry => stocks[entry.Query.RequestId]).ToArray();

    public DadGatherPlanProbe(DadGatherPartyTask task, IReadOnlyList<DadParticipantSnapshot> participants, string runId, DateTime nowUtc)
    {
        this.task = new();
        queries = [];
        startedUtc = nowUtc;
        if (!DadGatherPartyPlanningRules.TryValidateTask(task, out var taskError)) { Error = taskError; return; }
        this.task = task.Clone();
        var itemIds = task.Items.Where(item => item is { Enabled: true }).Select(item => item.ItemId).ToList();
        queries = participants.Select(participant => (participant.Clone(), new DadGatherStockQuery
        {
            RunId = runId, SlotId = participant.AssignedSlotId, WorkerSessionId = participant.WorkerSessionId,
            AccountKey = participant.ManagedAccountKey, CharacterKey = participant.ActiveCharacterKey,
            RequestedAtUtc = nowUtc, ItemIds = [..itemIds],
        })).ToList();
        if (participants.Count is < 1 or > DadGatherPartyPlanningRules.MaximumClients || itemIds.Count is < 1 or > DadGatherPartyPlanningRules.MaximumItems ||
            itemIds.Any(id => id == 0) || itemIds.Distinct().Count() != itemIds.Count)
            Error = "Gathering needs valid materials and selected clients within DAD's slot limit.";
    }

    public void Update(Func<DadParticipantSnapshot, DadGatherStockQuery, DadGatherStockReply?> send,
        DateTime nowUtc, TimeSpan timeout)
    {
        if (!Pending) return;
        if (nowUtc - startedUtc >= timeout)
        {
            Error = "Gathering stock query timed out. Check that every selected DAD worker supports gathering and is connected.";
            return;
        }
        foreach (var (participant, query) in queries)
        {
            if (stocks.ContainsKey(query.RequestId)) continue;
            var reply = send(participant, query);
            if (reply == null) continue;
            if (!DadGatherStockQueryRules.TryRead(query, reply, nowUtc, out var stock, out var error))
            {
                Error = $"{query.SlotId}: {error}";
                return;
            }
            stocks.Add(query.RequestId, stock!);
        }
        if (stocks.Count == queries.Count)
            Preview = DadGatherPartyPlanningRules.Preview(task, queries.Select(entry => stocks[entry.Query.RequestId]).ToArray());
    }
}
