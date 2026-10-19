using dad.Models;

namespace dad.Services;

// A retry needs repeated live observations, not elapsed time since command acceptance.
internal sealed class DadSafeIdleRetryGate
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);
    private string signature = string.Empty;
    private DateTime? sinceUtc;
    private DateTime? lastUtc;
    private DateTime? firstObservedUtc;
    private DateTime? lastObservedUtc;

    public bool Observe(DadParticipantSnapshot participant, DateTime nowUtc)
    {
        if (!participant.IsAvailable || !participant.WorldReadyStable ||
            !DadCoordinatorTravelRules.IsFreshComplete(participant.CurrentLocation, nowUtc))
        {
            Reset();
            return false;
        }

        var location = participant.CurrentLocation!;
        var current = $"{Identity(participant)}|{location.WorldId}|{location.WorldName}|" +
                      $"{location.DataCenterId}|{location.DataCenterName}|{location.RegionId}|{location.RegionName}";
        if (!string.Equals(signature, current, StringComparison.OrdinalIgnoreCase) ||
            !lastUtc.HasValue || nowUtc < lastUtc.Value ||
            (lastObservedUtc.HasValue && location.ObservedAtUtc < lastObservedUtc.Value) ||
            nowUtc - lastUtc.Value > DadCoordinatorTravelRules.MaxLocationProofAge)
        {
            signature = current;
            sinceUtc = nowUtc;
            firstObservedUtc = location.ObservedAtUtc;
        }
        lastUtc = nowUtc;
        lastObservedUtc = location.ObservedAtUtc;
        return sinceUtc.HasValue && nowUtc - sinceUtc.Value >= Delay &&
               firstObservedUtc.HasValue && location.ObservedAtUtc - firstObservedUtc.Value >= Delay;
    }

    public void Reset()
    {
        signature = string.Empty;
        sinceUtc = null;
        lastUtc = null;
        firstObservedUtc = null;
        lastObservedUtc = null;
    }

    internal static string Identity(DadParticipantSnapshot participant)
        => $"{participant.WorkerSessionId.Value}|{participant.ManagedAccountKey.Value}|" +
           $"{participant.ActiveCharacterKey.Value}|{participant.Character.CharacterKey}|" +
           $"{participant.Character.ContentId}|{participant.Character.WorldId}|{participant.Character.WorldName}";
}
