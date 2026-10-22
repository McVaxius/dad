using dad.Models;

namespace dad.Windows;

public sealed record DadAccountSummaryRow(
    DadAccountKey AccountKey,
    DadParticipantSnapshot Participant,
    bool HasMultipleLiveSessions);

public sealed record DadAccountSummaryProjection(
    IReadOnlyList<DadAccountSummaryRow> Accounts,
    IReadOnlyList<DadParticipantSnapshot> Unidentified)
{
    public static DadAccountSummaryProjection Build(
        DadParticipantSnapshot local,
        IEnumerable<DadParticipantSnapshot> observations,
        DateTime nowUtc)
    {
        var sessions = new List<DadParticipantSnapshot>();
        foreach (var observation in observations.Prepend(local))
        {
            var index = observation.WorkerSessionId.IsEmpty ? -1 : sessions.FindIndex(existing =>
                string.Equals(existing.WorkerSessionId.Value, observation.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                sessions.Add(observation);
            else if (!string.Equals(observation.WorkerSessionId.Value, local.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase)
                     && observation.LastHeartbeatUtc > sessions[index].LastHeartbeatUtc)
                sessions[index] = observation;
        }

        var accounts = new List<List<DadParticipantSnapshot>>();
        foreach (var session in sessions.Where(static participant => !participant.ManagedAccountKey.IsEmpty))
        {
            var account = accounts.Find(group => DadRosterIdentity.SameAccount(group[0].ManagedAccountKey, session.ManagedAccountKey));
            if (account == null)
                accounts.Add([session]);
            else
                account.Add(session);
        }

        return new DadAccountSummaryProjection(
            accounts.Select(group =>
            {
                var live = group.Where(participant => participant.State != DadParticipantState.Stale
                    && nowUtc - participant.LastHeartbeatUtc <= TimeSpan.FromSeconds(30)).ToList();
                var representative = (live.Count > 0 ? live : group).OrderByDescending(static participant => participant.LastHeartbeatUtc).First();
                return new DadAccountSummaryRow(new DadAccountKey(representative.ManagedAccountKey.Value.Trim()), representative, live.Count > 1);
            }).OrderBy(static row => row.Participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
              .ThenBy(static row => row.AccountKey.Value, StringComparer.OrdinalIgnoreCase).ToList(),
            sessions.Where(static participant => participant.ManagedAccountKey.IsEmpty).ToList());
    }
}
