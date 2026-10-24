using dad.Models;

namespace dad.Services;

public static class DadTrustProgressRules
{
    public static DadTrustProgress? ForOwner(DadTrustProgress? source, DadAccountKey account,
        DadCharacterKey character, ulong contentId)
    {
        if (source == null || account.IsEmpty || character.IsEmpty || contentId == 0 ||
            !DadRosterIdentity.SameAccount(source.AccountKey, account) || source.ContentId != contentId ||
            !DadRosterIdentity.SameCharacter(source.CharacterKey, source.ContentId, character, contentId))
            return null;

        var result = new DadTrustProgress { AccountKey = account, CharacterKey = character, ContentId = contentId,
            RosterObservedAtUtc = source.RosterObservedAtUtc is { } rosterTime && ValidTime(rosterTime) ? rosterTime : null };
        if ((source.Npcs ?? []).Any(static npc => npc == null || !KnownNpc(npc.NpcId) || !ValidTime(npc.ObservedAtUtc)))
            result.RosterObservedAtUtc = null;
        foreach (var group in (source.Npcs ?? []).Where(static npc => npc != null &&
                     KnownNpc(npc.NpcId) && ValidTime(npc.ObservedAtUtc)).GroupBy(static npc => npc.NpcId))
        {
            var latest = group.Max(static npc => npc.ObservedAtUtc);
            var readings = group.Where(npc => npc.ObservedAtUtc == latest).Select(static npc => npc with
            {
                DisplayName = npc.DisplayName?.Trim() ?? string.Empty,
                Level = ValidLevel(npc.Level) ? npc.Level : null,
                LevelCap = ValidLevel(npc.LevelCap) && (npc.Level == null || npc.LevelCap >= npc.Level) ? npc.LevelCap : null,
            }).Distinct().ToArray();
            // Conflicting readings from the same observation cannot establish a level.
            if (readings.Length == 1) result.Npcs.Add(readings[0]);
            else result.RosterObservedAtUtc = null;
        }
        foreach (var group in (source.Duties ?? []).Where(static duty => duty != null &&
                     duty.ContentFinderConditionId != 0 && ValidTime(duty.ObservedAtUtc))
                 .GroupBy(static duty => duty.ContentFinderConditionId))
        {
            var latest = group.Max(static duty => duty.ObservedAtUtc);
            var readings = group.Where(duty => duty.ObservedAtUtc == latest).Select(NormalizeDuty).ToArray();
            if (readings.All(duty => SameDuty(duty, readings[0]))) result.Duties.Add(readings[0]);
        }
        result.Npcs = result.Npcs.OrderBy(static npc => npc.NpcId).ToList();
        result.Duties = result.Duties.OrderBy(static duty => duty.ContentFinderConditionId).ToList();
        return result.Npcs.Count == 0 && result.Duties.Count == 0 && result.RosterObservedAtUtc == null ? null : result;
    }

    public static DadTrustProgress? Merge(DadTrustProgress? left, DadTrustProgress? right,
        DadAccountKey account, DadCharacterKey character, ulong contentId)
    {
        var first = ForOwner(left, account, character, contentId);
        var second = ForOwner(right, account, character, contentId);
        if (first == null) return second;
        if (second == null) return first;
        first.Npcs.AddRange(second.Npcs);
        first.Duties.AddRange(second.Duties);
        if (second.RosterObservedAtUtc is { } later && (first.RosterObservedAtUtc == null || later > first.RosterObservedAtUtc))
            first.RosterObservedAtUtc = later;
        return ForOwner(first, account, character, contentId);
    }

    public static bool IsFresh(DateTime observedAtUtc, DateTime nowUtc, TimeSpan maximumAge)
        => ValidTime(observedAtUtc) && nowUtc.Kind == DateTimeKind.Utc && maximumAge >= TimeSpan.Zero &&
           observedAtUtc <= nowUtc && nowUtc - observedAtUtc <= maximumAge;

    public static bool Same(DadTrustProgress? left, DadTrustProgress? right)
        => ReferenceEquals(left, right) || left != null && right != null &&
           DadRosterIdentity.SameAccount(left.AccountKey, right.AccountKey) &&
           left.CharacterKey == right.CharacterKey && left.ContentId == right.ContentId &&
           left.RosterObservedAtUtc == right.RosterObservedAtUtc &&
           (left.Npcs ?? []).SequenceEqual(right.Npcs ?? []) &&
           (left.Duties ?? []).Count == (right.Duties ?? []).Count &&
           (left.Duties ?? []).Zip(right.Duties ?? []).All(pair => SameDuty(pair.First, pair.Second));

    private static DadTrustDutyObservation NormalizeDuty(DadTrustDutyObservation source)
    {
        var result = source.Clone();
        if (source.Members != null && (source.Members.Any(static member => member == null ||
                !KnownNpc(member.NpcId) || (member.AvailableRoles & ~DadTrustRoles.All) != 0) ||
            source.Members.Select(static member => member.NpcId).Distinct().Count() != source.Members.Count))
            result.Members = null;
        if (result.Members != null) result.Members = result.Members.OrderBy(static member => member.NpcId).ToList();
        return result;
    }

    private static bool SameDuty(DadTrustDutyObservation left, DadTrustDutyObservation right)
        => left.ContentFinderConditionId == right.ContentFinderConditionId && left.Unlocked == right.Unlocked &&
           left.ObservedAtUtc == right.ObservedAtUtc && (left.Members == null) == (right.Members == null) &&
           (left.Members ?? []).SequenceEqual(right.Members ?? []);

    private static bool KnownNpc(DadTrustNpcId npc) => npc != DadTrustNpcId.Unknown && Enum.IsDefined(npc);
    private static bool ValidLevel(int? level) => level is > 0 and <= 999;
    private static bool ValidTime(DateTime time) => time.Kind == DateTimeKind.Utc && time > DateTime.MinValue;
}
