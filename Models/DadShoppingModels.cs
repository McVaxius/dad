using dad.Services;

namespace dad.Models;

public enum DadShoppingAssociationOwnerKind
{
    Plan = 0,
    Schedule = 1,
}

public sealed class DadShoppingAssociation
{
    public const int MaxCompletedRowIds = 512;

    public string AssociationId { get; set; } = Guid.NewGuid().ToString("N");
    public string PresetId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public string ShopperSlotId { get; set; } = string.Empty;
    public DadAccountKey ShopperAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey ShopperCharacterKey { get; set; } = new(string.Empty);
    public Dictionary<string, long>? CreditedQuantities { get; set; }
    public List<string> CompletedNonRepeatableRowIds { get; set; } = [];
    public bool NonRepeatableRowsFulfilled { get; set; }
    public DateTime? FulfilledAtUtc { get; set; }
    public bool RunAutoRetainerDelivery { get; set; }
    public string CustomCommand { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DadShoppingAssociation Normalize()
    {
        var resetAssociation = !Guid.TryParse(AssociationId?.Trim(), out var associationId) ||
                               associationId == Guid.Empty;
        AssociationId = resetAssociation
            ? Guid.NewGuid().ToString("N")
            : associationId.ToString("N");
        PresetId = DadShoppingAssociationRules.NormalizeAdsGuid(PresetId);
        PresetName = PresetName?.Trim() ?? string.Empty;
        ShopperSlotId = DadPlannerSlotRules.NormalizeStrictSlotId(ShopperSlotId);
        ShopperAccountKey = new DadAccountKey((ShopperAccountKey.Value ?? string.Empty).Trim());
        ShopperCharacterKey = new DadCharacterKey((ShopperCharacterKey.Value ?? string.Empty).Trim());
        CompletedNonRepeatableRowIds = (CompletedNonRepeatableRowIds ?? [])
            .Select(DadShoppingAssociationRules.NormalizeAdsGuid)
            .Where(static rowId => !string.IsNullOrWhiteSpace(rowId))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCompletedRowIds)
            .ToList();
        if (resetAssociation)
            ResetCompletionState();
        CustomCommand = CustomCommand?.Trim() ?? string.Empty;
        if (!NonRepeatableRowsFulfilled)
            FulfilledAtUtc = null;
        UpdatedAtUtc = EnsureUtc(UpdatedAtUtc == default ? DateTime.UtcNow : UpdatedAtUtc);
        return this;
    }

    public DadShoppingAssociation Clone()
        => new()
        {
            AssociationId = AssociationId,
            PresetId = PresetId,
            PresetName = PresetName,
            ShopperSlotId = ShopperSlotId,
            ShopperAccountKey = ShopperAccountKey,
            ShopperCharacterKey = ShopperCharacterKey,
            CreditedQuantities = CreditedQuantities == null ? null : new(CreditedQuantities),
            CompletedNonRepeatableRowIds = [..CompletedNonRepeatableRowIds],
            NonRepeatableRowsFulfilled = NonRepeatableRowsFulfilled,
            FulfilledAtUtc = FulfilledAtUtc,
            RunAutoRetainerDelivery = RunAutoRetainerDelivery,
            CustomCommand = CustomCommand,
            UpdatedAtUtc = UpdatedAtUtc,
        };

    public void ResetCompletionState()
    {
        CreditedQuantities = null;
        CompletedNonRepeatableRowIds = [];
        NonRepeatableRowsFulfilled = false;
        FulfilledAtUtc = null;
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

public sealed class DadShoppingRunAssociation
{
    public DadShoppingAssociationOwnerKind OwnerKind { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public long OwnerRevision { get; set; }
    public string AssociationId { get; set; } = string.Empty;
    public string PresetId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public string ShopperSlotId { get; set; } = string.Empty;
    public DadAccountKey ShopperAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey ShopperCharacterKey { get; set; } = new(string.Empty);
    public Dictionary<string, long>? CreditedQuantities { get; set; }
    public List<string> CompletedNonRepeatableRowIds { get; set; } = [];
    public bool NonRepeatableRowsFulfilled { get; set; }
    public bool RunAutoRetainerDelivery { get; set; }
    public string CustomCommand { get; set; } = string.Empty;

    public DadShoppingRunAssociation Normalize()
    {
        OwnerId = OwnerId?.Trim() ?? string.Empty;
        OwnerName = OwnerName?.Trim() ?? string.Empty;
        AssociationId = AssociationId?.Trim() ?? string.Empty;
        PresetId = DadShoppingAssociationRules.NormalizeAdsGuid(PresetId);
        PresetName = PresetName?.Trim() ?? string.Empty;
        ShopperSlotId = DadPlannerSlotRules.NormalizeStrictSlotId(ShopperSlotId);
        ShopperAccountKey = new DadAccountKey((ShopperAccountKey.Value ?? string.Empty).Trim());
        ShopperCharacterKey = new DadCharacterKey((ShopperCharacterKey.Value ?? string.Empty).Trim());
        CompletedNonRepeatableRowIds = (CompletedNonRepeatableRowIds ?? [])
            .Select(DadShoppingAssociationRules.NormalizeAdsGuid)
            .Where(static rowId => !string.IsNullOrWhiteSpace(rowId))
            .Distinct(StringComparer.Ordinal)
            .Take(DadShoppingAssociation.MaxCompletedRowIds)
            .ToList();
        CustomCommand = CustomCommand?.Trim() ?? string.Empty;
        OwnerRevision = Math.Max(0, OwnerRevision);
        return this;
    }

    public DadShoppingRunAssociation Clone()
        => new()
        {
            OwnerKind = OwnerKind,
            OwnerId = OwnerId,
            OwnerName = OwnerName,
            OwnerRevision = OwnerRevision,
            AssociationId = AssociationId,
            PresetId = PresetId,
            PresetName = PresetName,
            ShopperSlotId = ShopperSlotId,
            ShopperAccountKey = ShopperAccountKey,
            ShopperCharacterKey = ShopperCharacterKey,
            CreditedQuantities = CreditedQuantities == null ? null : new(CreditedQuantities),
            CompletedNonRepeatableRowIds = [..CompletedNonRepeatableRowIds],
            NonRepeatableRowsFulfilled = NonRepeatableRowsFulfilled,
            RunAutoRetainerDelivery = RunAutoRetainerDelivery,
            CustomCommand = CustomCommand,
        };
}

public sealed class DadShoppingFailureRecord
{
    public string FailureId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public string RunId { get; set; } = string.Empty;
    public int ModuleIndex { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public DadShoppingAssociationOwnerKind OwnerKind { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string AssociationId { get; set; } = string.Empty;
    public string PresetId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public string ShopperSlotId { get; set; } = string.Empty;
    public DadCharacterKey ShopperCharacterKey { get; set; } = new(string.Empty);
    public string FailureCode { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public bool Reviewed { get; set; }

    public DadShoppingFailureRecord Clone()
        => new()
        {
            FailureId = FailureId,
            ObservedAtUtc = ObservedAtUtc,
            RunId = RunId,
            ModuleIndex = ModuleIndex,
            OperationId = OperationId,
            OwnerKind = OwnerKind,
            OwnerId = OwnerId,
            OwnerName = OwnerName,
            AssociationId = AssociationId,
            PresetId = PresetId,
            PresetName = PresetName,
            ShopperSlotId = ShopperSlotId,
            ShopperCharacterKey = ShopperCharacterKey,
            FailureCode = FailureCode,
            Summary = Summary,
            Details = Details,
            Reviewed = Reviewed,
        };
}

public static class DadShoppingAssociationRules
{
    public static Dictionary<string, long>? MergeCreditedQuantities(
        IReadOnlyDictionary<string, long>? previous, IReadOnlyDictionary<string, long>? current)
        => current == null ? previous == null ? null : new(previous)
            : current.ToDictionary(pair => NormalizeAdsGuid(pair.Key), pair =>
                Math.Max(pair.Value, previous?.GetValueOrDefault(NormalizeAdsGuid(pair.Key)) ?? 0));

    public static bool ValidCreditedQuantities(IReadOnlyDictionary<string, long>? progress)
        => progress == null || progress.Count <= DadShoppingAssociation.MaxCompletedRowIds &&
            progress.All(pair => !string.IsNullOrEmpty(NormalizeAdsGuid(pair.Key)) && pair.Value is >= 0 and <= int.MaxValue) &&
            progress.Keys.Select(NormalizeAdsGuid).Distinct(StringComparer.Ordinal).Count() == progress.Count;

    public const int MaximumFailureRecords = 50;

    public static DadShoppingAssociation CopyChoicesToMember(DadShoppingAssociation source,
        DadPlannerGroupSlot slot, DadShoppingAssociation? previous)
    {
        var result = previous?.Clone() ?? new DadShoppingAssociation();
        result.PresetId = source.PresetId;
        result.PresetName = source.PresetName;
        result.CustomCommand = source.CustomCommand;
        result.RunAutoRetainerDelivery = source.RunAutoRetainerDelivery;
        result.ShopperSlotId = slot.SlotId;
        result.ShopperAccountKey = slot.RequiredAccountKey;
        result.ShopperCharacterKey = slot.RequiredCharacterKey;
        ResetCompletionIfProvenanceChanged(previous, result);
        return result;
    }

    public static string NormalizeAdsGuid(string? value)
        => Guid.TryParse(value?.Trim(), out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : string.Empty;

    public static DadShoppingRunAssociation? FreezePlan(DadPlannerGroup? plan)
        => FreezePlanAll(plan).FirstOrDefault();

    public static IReadOnlyList<DadShoppingAssociation> PlanAssignments(DadPlannerGroup? plan)
        => Assignments(plan?.ShoppingAssociation, plan?.ShoppingAssociations);

    public static IReadOnlyList<DadShoppingAssociation> ScheduleAssignments(DadScheduleDefinition? schedule)
        => Assignments(schedule?.ShoppingAssociation, schedule?.ShoppingAssociations);

    private static IReadOnlyList<DadShoppingAssociation> Assignments(DadShoppingAssociation? legacy,
        IEnumerable<DadShoppingAssociation>? members)
        => (legacy == null ? Enumerable.Empty<DadShoppingAssociation>() : new[] { legacy })
            .Concat(members ?? []).Where(static value => value != null).ToList();

    public static List<DadShoppingRunAssociation> FreezePlanAll(DadPlannerGroup? plan)
        => plan == null ? [] : PlanAssignments(plan).Select(source => Freeze(
                DadShoppingAssociationOwnerKind.Plan,
                plan.GroupId,
                plan.DisplayName,
                Math.Max(0, plan.UpdatedAtUtc.Ticks),
                source)).ToList();

    public static DadShoppingRunAssociation? FreezeSchedule(DadScheduleDefinition? schedule)
        => FreezeScheduleAll(schedule).FirstOrDefault();

    public static List<DadShoppingRunAssociation> FreezeScheduleAll(DadScheduleDefinition? schedule)
        => schedule == null ? [] : ScheduleAssignments(schedule).Select(source => Freeze(
                DadShoppingAssociationOwnerKind.Schedule,
                schedule.ScheduleId,
                schedule.DisplayName,
                schedule.Revision,
                source)).ToList();

    public static List<DadShoppingRunAssociation> NormalizeRunAssociations(
        IEnumerable<DadShoppingRunAssociation>? associations)
        => (associations ?? [])
            .Where(static association => association != null)
            .Select(static association => association.Normalize())
            .Where(static association =>
                !string.IsNullOrWhiteSpace(association.OwnerId) &&
                Guid.TryParseExact(association.AssociationId, "N", out var associationId) &&
                associationId != Guid.Empty &&
                (!string.IsNullOrWhiteSpace(association.PresetId) || !string.IsNullOrWhiteSpace(association.CustomCommand)))
            .GroupBy(static association =>
                $"{(int)association.OwnerKind}|{association.OwnerId}|{association.AssociationId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static association => association.OwnerKind)
            .ToList();

    public static bool TryValidateForPlan(
        DadShoppingRunAssociation association,
        DadPlannerGroup plan,
        out string blocker)
    {
        blocker = string.Empty;
        if (association == null)
            return Fail("Shopping association is missing.", out blocker);
        if (!Enum.IsDefined(association.OwnerKind) || string.IsNullOrWhiteSpace(association.OwnerId))
            return Fail("Shopping association has invalid owner provenance.", out blocker);
        if (association.OwnerKind == DadShoppingAssociationOwnerKind.Plan &&
            !string.Equals(association.OwnerId, plan.GroupId, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Plan shopping association owner does not match the selected Plan.", out blocker);
        }
        if (!Guid.TryParseExact(association.AssociationId, "N", out var associationId) ||
            associationId == Guid.Empty ||
            !string.Equals(association.AssociationId, associationId.ToString("N"), StringComparison.Ordinal))
        {
            return Fail("Shopping association has no canonical AssociationId GUID.", out blocker);
        }
        if (association.PresetId.Length > 0 && string.IsNullOrWhiteSpace(NormalizeAdsGuid(association.PresetId)) ||
            association.PresetId.Length == 0 && string.IsNullOrWhiteSpace(association.CustomCommand))
            return Fail("A member assignment requires a valid ADS list or a final slash command.", out blocker);
        if (association.PresetId.Length == 0 && association.RunAutoRetainerDelivery)
            return Fail("AutoRetainer delivery requires a shopping list.", out blocker);
        if (!DadPlannerSlotRules.TryParseStrictSlotNumber(association.ShopperSlotId, out _))
            return Fail("Shopping association has no exact shopper slot.", out blocker);
        if (association.ShopperAccountKey.IsEmpty || association.ShopperCharacterKey.IsEmpty)
            return Fail("Shopping association requires an exact LAN shopper account and character.", out blocker);
        if (!string.IsNullOrWhiteSpace(association.CustomCommand) &&
            !DadCompletionCommandRules.TryNormalizeCustomCommand(
                association.CustomCommand,
                out _,
                out blocker))
        {
            return false;
        }

        var matches = DadPlannerSlotRules.NormalizeGroupSlots(plan.Slots)
            .Where(static slot => !slot.IsSubstitute)
            .Where(slot => string.Equals(slot.SlotId, association.ShopperSlotId, StringComparison.OrdinalIgnoreCase))
            .Where(slot => DadRosterIdentity.SameAccount(slot.RequiredAccountKey, association.ShopperAccountKey))
            .Where(slot => string.Equals(
                slot.RequiredCharacterKey.Value,
                association.ShopperCharacterKey.Value,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
            return Fail($"Shopping association shopper must resolve to one exact primary Plan row; found {matches.Count}.", out blocker);
        if (matches[0].SharedIdentity is { IdentityToken: { Length: > 0 } })
            return Fail("Registered Dad-island characters cannot be selected as shoppers.", out blocker);
        return true;
    }

    public static bool TryValidateFrozenParticipants(
        IEnumerable<DadShoppingRunAssociation>? associations,
        IReadOnlyCollection<DadParticipantSnapshot> participants,
        out string blocker)
    {
        blocker = string.Empty;
        var normalized = NormalizeRunAssociations(associations);
        if (!TryValidateSingleShopper(normalized, out blocker))
            return false;
        foreach (var association in normalized)
        {
            var matches = participants.Where(participant =>
                    string.Equals(participant.AssignedSlotId, association.ShopperSlotId, StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(participant.RegisteredIslandId) &&
                    DadRosterIdentity.SameAccount(participant.ManagedAccountKey, association.ShopperAccountKey) &&
                    string.Equals(
                        participant.ActiveCharacterKey.Value,
                        association.ShopperCharacterKey.Value,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                return Fail(
                    $"{association.OwnerKind} shopping association '{association.OwnerName}' requires one exact local/LAN shopper " +
                    $"at {association.ShopperSlotId}; found {matches.Count}. Registered Dad-island participants are never shoppers.",
                    out blocker);
            }
        }
        return true;
    }

    public static bool TryValidateSingleShopper(
        IEnumerable<DadShoppingRunAssociation>? associations,
        out string blocker)
    {
        // Retain the existing entry point for older callers. Independent exact
        // shoppers are now supported; one owner cannot assign a member twice.
        var raw = (associations ?? []).ToList();
        var normalized = NormalizeRunAssociations(raw);
        blocker = string.Empty;
        if (raw.Count != normalized.Count)
            return Fail("Member assignments contain duplicate or malformed provenance.", out blocker);
        if (normalized.GroupBy(value => $"{(int)value.OwnerKind}|{value.OwnerId}|{value.ShopperSlotId}",
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return Fail("Each Plan or Schedule member can have only one shopping/command assignment.", out blocker);
        return true;
    }

    public static bool SameShopper(DadShoppingRunAssociation left, DadShoppingRunAssociation right)
        => string.Equals(left.ShopperSlotId, right.ShopperSlotId, StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(left.ShopperAccountKey, right.ShopperAccountKey) &&
           string.Equals(
               left.ShopperCharacterKey.Value,
               right.ShopperCharacterKey.Value,
               StringComparison.OrdinalIgnoreCase);

    public static bool TryValidateForSchedule(
        DadScheduleDefinition schedule,
        IEnumerable<DadPlannerGroup>? plans,
        out string blocker)
    {
        blocker = string.Empty;
        var associations = FreezeScheduleAll(schedule);
        if (associations.Count == 0)
            return true;
        if (!TryValidateSingleShopper(associations, out blocker)) return false;

        var referenced = ResolveReferencedSchedulePlans(schedule, plans, out blocker);
        if (referenced.Count == 0)
            return false;
        foreach (var plan in referenced)
        {
            foreach (var association in associations)
            {
                if (!TryValidateForPlan(association, plan, out var planBlocker))
                    return Fail($"Schedule member is not one exact common LAN row in Plan '{plan.DisplayName}': {planBlocker}", out blocker);
            }
        }
        return true;
    }

    public static IReadOnlyList<DadPlannerGroupSlot> ResolveCommonScheduleShopperSlots(
        DadScheduleDefinition schedule,
        IEnumerable<DadPlannerGroup>? plans)
    {
        var referenced = ResolveReferencedSchedulePlans(schedule, plans, out _);
        if (referenced.Count == 0)
            return [];

        var firstSlots = DadPlannerSlotRules.NormalizeGroupSlots(referenced[0].Slots)
            .Where(static slot =>
                !slot.IsSubstitute &&
                slot.SharedIdentity == null &&
                !slot.RequiredAccountKey.IsEmpty &&
                !slot.RequiredCharacterKey.IsEmpty)
            .ToList();
        return firstSlots
            .Where(candidate => referenced.Skip(1).All(plan =>
                DadPlannerSlotRules.NormalizeGroupSlots(plan.Slots).Count(slot =>
                    !slot.IsSubstitute &&
                    slot.SharedIdentity == null &&
                    string.Equals(slot.SlotId, candidate.SlotId, StringComparison.OrdinalIgnoreCase) &&
                    DadRosterIdentity.SameAccount(slot.RequiredAccountKey, candidate.RequiredAccountKey) &&
                    string.Equals(
                        slot.RequiredCharacterKey.Value,
                        candidate.RequiredCharacterKey.Value,
                        StringComparison.OrdinalIgnoreCase)) == 1))
            .Select(CloneShopperSlot)
            .ToList();
    }

    public static bool TryBindToSchedulePlans(
        DadShoppingAssociation? association,
        DadScheduleDefinition schedule,
        IEnumerable<DadPlannerGroup>? plans,
        out bool changed)
    {
        changed = false;
        if (association == null)
            return false;
        association.Normalize();
        var slot = ResolveCommonScheduleShopperSlots(schedule, plans)
            .SingleOrDefault(candidate => string.Equals(
                candidate.SlotId,
                association.ShopperSlotId,
                StringComparison.OrdinalIgnoreCase));
        if (slot == null)
            return false;

        var identityChanged = !DadRosterIdentity.SameAccount(
                                  association.ShopperAccountKey,
                                  slot.RequiredAccountKey) ||
                              !string.Equals(
                                  association.ShopperCharacterKey.Value,
                                  slot.RequiredCharacterKey.Value,
                                  StringComparison.OrdinalIgnoreCase);
        association.ShopperAccountKey = slot.RequiredAccountKey;
        association.ShopperCharacterKey = slot.RequiredCharacterKey;
        if (identityChanged)
            association.ResetCompletionState();
        changed = identityChanged;
        return true;
    }

    public static bool MatchesLocalShopper(
        DadShoppingRunAssociation association,
        DadParticipantSnapshot participant)
        => string.IsNullOrWhiteSpace(participant.RegisteredIslandId) &&
           string.Equals(participant.AssignedSlotId, association.ShopperSlotId, StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(participant.ManagedAccountKey, association.ShopperAccountKey) &&
           string.Equals(
               participant.ActiveCharacterKey.Value,
               association.ShopperCharacterKey.Value,
               StringComparison.OrdinalIgnoreCase);

    public static bool SameProvenance(
        DadShoppingRunAssociation frozen,
        DadShoppingAssociation persisted)
        => string.Equals(frozen.AssociationId, persisted.AssociationId, StringComparison.Ordinal) &&
           string.Equals(frozen.PresetId, persisted.PresetId, StringComparison.Ordinal) &&
           string.Equals(frozen.ShopperSlotId, persisted.ShopperSlotId, StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(frozen.ShopperAccountKey, persisted.ShopperAccountKey) &&
           string.Equals(
               frozen.ShopperCharacterKey.Value,
               persisted.ShopperCharacterKey.Value,
               StringComparison.OrdinalIgnoreCase);

    public static bool SameProvenance(
        DadShoppingRunAssociation left,
        DadShoppingRunAssociation right)
        => string.Equals(left.AssociationId, right.AssociationId, StringComparison.Ordinal) &&
           string.Equals(left.PresetId, right.PresetId, StringComparison.Ordinal) &&
           SameShopper(left, right);

    public static bool TryBindToPlanSlot(DadShoppingAssociation? association, DadPlannerGroup plan)
        => TryBindToPlanSlot(association, plan, out _);

    public static bool TryBindToPlanSlot(
        DadShoppingAssociation? association,
        DadPlannerGroup plan,
        out bool changed)
    {
        changed = false;
        if (association == null)
            return false;
        association.Normalize();
        var slot = DadPlannerSlotRules.NormalizeGroupSlots(plan.Slots)
            .SingleOrDefault(candidate =>
                !candidate.IsSubstitute &&
                candidate.SharedIdentity == null &&
                string.Equals(candidate.SlotId, association.ShopperSlotId, StringComparison.OrdinalIgnoreCase));
        if (slot == null || slot.RequiredAccountKey.IsEmpty || slot.RequiredCharacterKey.IsEmpty)
            return false;

        var identityChanged = !DadRosterIdentity.SameAccount(
                                  association.ShopperAccountKey,
                                  slot.RequiredAccountKey) ||
                              !string.Equals(
                                  association.ShopperCharacterKey.Value,
                                  slot.RequiredCharacterKey.Value,
                                  StringComparison.OrdinalIgnoreCase);
        association.ShopperAccountKey = slot.RequiredAccountKey;
        association.ShopperCharacterKey = slot.RequiredCharacterKey;
        if (identityChanged)
            association.ResetCompletionState();
        changed = identityChanged;
        return true;
    }


    public static bool ResetCompletionIfProvenanceChanged(
        DadShoppingAssociation? previous,
        DadShoppingAssociation current)
    {
        if (previous == null)
            return false;
        var changed = !string.Equals(previous.PresetId, current.PresetId, StringComparison.Ordinal) ||
                      !string.Equals(previous.AssociationId, current.AssociationId, StringComparison.Ordinal) ||
                      !string.Equals(previous.ShopperSlotId, current.ShopperSlotId, StringComparison.OrdinalIgnoreCase) ||
                      !DadRosterIdentity.SameAccount(previous.ShopperAccountKey, current.ShopperAccountKey) ||
                      !string.Equals(
                          previous.ShopperCharacterKey.Value,
                          current.ShopperCharacterKey.Value,
                          StringComparison.OrdinalIgnoreCase);
        if (changed)
            current.ResetCompletionState();
        return changed;
    }

    public static void TrimFailures(List<DadShoppingFailureRecord> failures)
    {
        failures.RemoveAll(static failure => failure == null);
        failures.Sort(static (left, right) => right.ObservedAtUtc.CompareTo(left.ObservedAtUtc));
        if (failures.Count <= MaximumFailureRecords)
            return;
        failures.RemoveRange(MaximumFailureRecords, failures.Count - MaximumFailureRecords);
    }

    private static DadShoppingRunAssociation Freeze(
        DadShoppingAssociationOwnerKind ownerKind,
        string ownerId,
        string ownerName,
        long ownerRevision,
        DadShoppingAssociation source)
    {
        var normalized = source.Clone().Normalize();
        return new DadShoppingRunAssociation
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId?.Trim() ?? string.Empty,
            OwnerName = ownerName?.Trim() ?? string.Empty,
            OwnerRevision = ownerRevision,
            AssociationId = normalized.AssociationId,
            PresetId = normalized.PresetId,
            PresetName = normalized.PresetName,
            ShopperSlotId = normalized.ShopperSlotId,
            ShopperAccountKey = normalized.ShopperAccountKey,
            ShopperCharacterKey = normalized.ShopperCharacterKey,
            CreditedQuantities = normalized.CreditedQuantities == null ? null : new(normalized.CreditedQuantities),
            CompletedNonRepeatableRowIds = [..normalized.CompletedNonRepeatableRowIds],
            NonRepeatableRowsFulfilled = normalized.NonRepeatableRowsFulfilled,
            RunAutoRetainerDelivery = normalized.RunAutoRetainerDelivery,
            CustomCommand = normalized.CustomCommand,
        }.Normalize();
    }

    private static List<DadPlannerGroup> ResolveReferencedSchedulePlans(
        DadScheduleDefinition schedule,
        IEnumerable<DadPlannerGroup>? plans,
        out string blocker)
    {
        blocker = string.Empty;
        var available = (plans ?? []).Where(static plan => plan != null).ToList();
        var result = new List<DadPlannerGroup>();
        foreach (var groupId in (schedule.Entries ?? [])
                     .Select(static entry => entry.GroupId?.Trim() ?? string.Empty)
                     .Where(static groupId => !string.IsNullOrWhiteSpace(groupId))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matches = available.Where(plan => string.Equals(
                plan.GroupId,
                groupId,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                blocker = $"Schedule shopping requires every referenced Plan exactly once; '{groupId}' resolved {matches.Count} time(s).";
                return [];
            }
            result.Add(matches[0]);
        }

        if (result.Count == 0)
            blocker = "Schedule shopping requires at least one referenced Plan.";
        return result;
    }

    private static DadPlannerGroupSlot CloneShopperSlot(DadPlannerGroupSlot source)
        => new()
        {
            SlotId = source.SlotId,
            IsSubstitute = source.IsSubstitute,
            AllianceAssignment = source.AllianceAssignment,
            RequiredRole = source.RequiredRole,
            RequiredAccountKey = source.RequiredAccountKey,
            RequiredCharacterKey = source.RequiredCharacterKey,
            RequiredJobId = source.RequiredJobId,
            AdsLootMode = source.AdsLootMode,
            LevelSeekTarget = source.LevelSeekTarget,
            SkipIfDailyRouletteRewardReceived = source.SkipIfDailyRouletteRewardReceived,
            WakePolicy = source.WakePolicy,
            LaunchProfileId = source.LaunchProfileId,
            CharacterLoadInstruction = source.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction(),
            SharedIdentity = source.SharedIdentity?.Clone(),
            AllowSubstitution = source.AllowSubstitution,
        };

    private static bool Fail(string summary, out string blocker)
    {
        blocker = summary;
        return false;
    }
}

public sealed class DadAdsShopListPresetCatalog
{
    public int Version { get; set; }
    public string ActivePresetId { get; set; } = string.Empty;
    public List<DadAdsShopListPresetSummary> Presets { get; set; } = [];
}

public sealed class DadAdsShopListPresetSummary
{
    public string PresetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string CurrencyKind { get; set; } = string.Empty;
    public uint CurrencyItemId { get; set; }
    public long CurrencyThreshold { get; set; }
    public int RowCount { get; set; }
}

public sealed class DadAdsShopListPresetRequest
{
    public int Version { get; set; } = 1;
    public string OperationId { get; set; } = string.Empty;
    public string PresetId { get; set; } = string.Empty;
    public bool SupportsFiniteOrderProgress { get; set; } = true;
    public Dictionary<string, long>? CreditedQuantities { get; set; }
    public List<string> CompletedRowIds { get; set; } = [];
}

public sealed class DadAdsShopListPreviewResponse
{
    public int Version { get; set; }
    public string PresetId { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public long CurrencyAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, long>? CreditedQuantities { get; set; }
    public List<string> CompletedNonRepeatableRowIds { get; set; } = [];
    public List<DadAdsShopListPreviewRow> Rows { get; set; } = [];
}

public sealed class DadAdsShopListPreviewRow
{
    public string RowId { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int TriggerBelow { get; set; }
    public int RefillToAtLeast { get; set; }
    public bool Repeatable { get; set; }
    public string OwnershipScope { get; set; } = string.Empty;
    public long LiveInventoryQuantity { get; set; }
    public long RetainerQuantity { get; set; }
    public long OwnedQuantity { get; set; }
    public long PurchaseQuantity { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

public sealed class DadAdsShopListStartResponse
{
    public int Version { get; set; }
    public bool Accepted { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string PresetId { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, long>? CreditedQuantities { get; set; }
    public List<string> CompletedNonRepeatableRowIds { get; set; } = [];
}

public sealed class DadAdsShopListStatusResponse
{
    public int Version { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string PresetId { get; set; } = string.Empty;
    public bool Running { get; set; }
    public bool Done { get; set; }
    public bool? Succeeded { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public Dictionary<string, long>? CreditedQuantities { get; set; }
    public List<string> CompletedNonRepeatableRowIds { get; set; } = [];
    public List<string> SkippedRowIds { get; set; } = [];
    public string FailureCode { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public string FailureMessage { get; set; } = string.Empty;
    public List<DadAdsShopListRowStatus> Rows { get; set; } = [];
}

public sealed class DadAdsShopListRowStatus
{
    public string RowId { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool Repeatable { get; set; }
    public string OwnershipScope { get; set; } = string.Empty;
    public int TriggerBelow { get; set; }
    public int RefillToAtLeast { get; set; }
    public long OwnedQuantity { get; set; }
    public long RequestedQuantity { get; set; }
    public long PurchasedQuantity { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public DadAdsShopListRowStatus Clone()
        => (DadAdsShopListRowStatus)MemberwiseClone();
}

public enum DadAdsShoppingStartOutcome
{
    Accepted = 0,
    NotTriggered = 1,
    Fulfilled = 2,
    Rejected = 3,
    Uncertain = 4,
    Partial = 5,
}

public readonly record struct DadAdsShoppingStartResult(
    DadAdsShoppingStartOutcome Outcome,
    DadAdsShopListStartResponse? Response,
    string Summary);

public readonly record struct DadAdsShoppingStatusResult(
    bool Readable,
    DadAdsShopListStatusResponse? Response,
    string Summary);

public readonly record struct DadAdsShoppingCatalogResult(
    bool Readable,
    DadAdsShopListPresetCatalog? Catalog,
    string Summary);

public readonly record struct DadAdsShoppingPreviewResult(
    bool Readable,
    DadAdsShopListPreviewResponse? Preview,
    string Summary);

public sealed class DadShoppingRunResult
{
    public string RunId { get; set; } = string.Empty;
    public int ModuleIndex { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public DadShoppingRunAssociation Association { get; set; } = new();
    public bool Succeeded { get; set; }
    public bool NonRepeatableRowsFulfilled { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public Dictionary<string, long>? CreditedQuantities { get; set; }
    public List<string> CompletedNonRepeatableRowIds { get; set; } = [];
    public List<DadAdsShopListRowStatus> Rows { get; set; } = [];
    public List<string> AcknowledgedPostCommands { get; set; } = [];
    public string FailureCode { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string FailureMessage { get; set; } = string.Empty;

    public DadShoppingRunResult Clone()
        => new()
        {
            RunId = RunId,
            ModuleIndex = ModuleIndex,
            OperationId = OperationId,
            Association = Association.Clone(),
            Succeeded = Succeeded,
            NonRepeatableRowsFulfilled = NonRepeatableRowsFulfilled,
            Disposition = Disposition,
            CreditedQuantities = CreditedQuantities == null ? null : new(CreditedQuantities),
            CompletedNonRepeatableRowIds = [..CompletedNonRepeatableRowIds],
            Rows = Rows.Select(static row => row.Clone()).ToList(),
            AcknowledgedPostCommands = [..AcknowledgedPostCommands],
            FailureCode = FailureCode,
            Summary = Summary,
            FailureMessage = FailureMessage,
        };
}

public enum DadShoppingRuntimeAction
{
    Ready = 0,
    Wait = 1,
    Reject = 2,
}

public readonly record struct DadShoppingRuntimeDecision(
    DadShoppingRuntimeAction Action,
    string Summary);
