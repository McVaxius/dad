using dad.Models;
using dad.Services;

namespace dad;

public sealed partial class Plugin
{
    public bool TrySaveMemberAssignments(DadShoppingAssociationOwnerKind kind, string ownerId,
        IReadOnlyList<DadShoppingAssociation> drafts, out string error)
    {
        error = GetShareMutationBlocker();
        if (error.Length > 0) return false;
        if (!Enum.IsDefined(kind)) { error = "Unknown member-assignment owner."; return false; }
        var plans = kind == DadShoppingAssociationOwnerKind.Plan
            ? Configuration.PlannerGroups.Where(value => string.Equals(value.GroupId, ownerId, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        var schedules = kind == DadShoppingAssociationOwnerKind.Schedule
            ? Configuration.Schedules.Where(value => string.Equals(value.ScheduleId, ownerId, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        if (plans.Count + schedules.Count != 1) { error = "The selected owner is missing or duplicated."; return false; }
        var plan = plans.SingleOrDefault(); var schedule = schedules.SingleOrDefault();
        if (plan == null && schedule == null || plan?.IsTemplate == true)
        { error = "Select an editable saved preset or schedule."; return false; }
        var previous = plan != null ? DadShoppingAssociationRules.PlanAssignments(plan) : DadShoppingAssociationRules.ScheduleAssignments(schedule);
        if (previous.GroupBy(value => value.AssociationId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        { error = "Existing member assignment IDs are duplicated."; return false; }
        var candidates = new List<DadShoppingAssociation>();
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.PresetId) && string.IsNullOrWhiteSpace(draft.CustomCommand)) continue;
            var existing = previous.SingleOrDefault(value => value.AssociationId == draft.AssociationId);
            if (!TryPrepareShoppingAssociation(draft, existing, out var candidate, out error)) return false;
            DadShoppingAssociationRules.ResetCompletionIfProvenanceChanged(existing, candidate);
            if (candidate.PresetId.Length > 0 && !ValidateShoppingCatalog(candidate, out error)) return false;
            candidates.Add(candidate);
        }
        if (candidates.Count > DadShareConstants.MaxSlotsPerPlan)
        { error = "Too many member assignments."; return false; }
        if (plan != null)
        {
            var preview = DadSchedulerGroupCloneRules.CloneWithSlots(plan, plan.Slots);
            preview.ShoppingAssociation = null; preview.ShoppingAssociations = candidates;
            var frozen = DadShoppingAssociationRules.FreezePlanAll(preview);
            if (!DadShoppingAssociationRules.TryValidateSingleShopper(frozen, out error)) return false;
            foreach (var assignment in frozen)
                if (!DadShoppingAssociationRules.TryValidateForPlan(assignment, preview, out error)) return false;
            var legacy = plan.ShoppingAssociation; var members = plan.ShoppingAssociations; var updated = plan.UpdatedAtUtc;
            plan.ShoppingAssociation = null; plan.ShoppingAssociations = candidates; plan.UpdatedAtUtc = DateTime.UtcNow;
            if (!Configuration.TryPersistNow())
            {
                plan.ShoppingAssociation = legacy; plan.ShoppingAssociations = members; plan.UpdatedAtUtc = updated;
                error = "Member assignments could not be persisted."; return false;
            }
        }
        else
        {
            var preview = schedule!.Clone(); preview.ShoppingAssociation = null; preview.ShoppingAssociations = candidates;
            if (!DadShoppingAssociationRules.TryValidateForSchedule(preview, Configuration.PlannerGroups, out error)) return false;
            var legacy = schedule.ShoppingAssociation; var members = schedule.ShoppingAssociations;
            var updated = schedule.UpdatedAtUtc; var revision = schedule.Revision;
            schedule.ShoppingAssociation = null; schedule.ShoppingAssociations = candidates;
            schedule.UpdatedAtUtc = DateTime.UtcNow; schedule.Revision++;
            if (!Configuration.TryPersistNow())
            {
                schedule.ShoppingAssociation = legacy; schedule.ShoppingAssociations = members;
                schedule.UpdatedAtUtc = updated; schedule.Revision = revision;
                error = "Member assignments could not be persisted."; return false;
            }
        }
        InvalidatePlannerPreviewCache("Member shopping and final commands updated");
        error = string.Empty;
        return true;
    }

    private bool SaveOneMemberAssignment(DadShoppingAssociationOwnerKind kind, string ownerId,
        DadShoppingAssociation draft, out string error)
    {
        var assignments = kind == DadShoppingAssociationOwnerKind.Plan
            ? DadShoppingAssociationRules.PlanAssignments(Configuration.PlannerGroups.FirstOrDefault(value => string.Equals(value.GroupId, ownerId, StringComparison.OrdinalIgnoreCase)))
            : DadShoppingAssociationRules.ScheduleAssignments(Configuration.Schedules.FirstOrDefault(value => string.Equals(value.ScheduleId, ownerId, StringComparison.OrdinalIgnoreCase)));
        var rows = assignments.Select(static value => value.Clone()).ToList();
        var selected = rows.SingleOrDefault(value => value.ShopperSlotId == draft.ShopperSlotId &&
            DadRosterIdentity.SameAccount(value.ShopperAccountKey, draft.ShopperAccountKey) &&
            string.Equals(value.ShopperCharacterKey.Value, draft.ShopperCharacterKey.Value, StringComparison.OrdinalIgnoreCase));
        var candidate = draft.Clone();
        if (selected != null) candidate.AssociationId = selected.AssociationId;
        else if (rows.Any(value => value.AssociationId == candidate.AssociationId))
        { candidate.AssociationId = Guid.NewGuid().ToString("N"); candidate.ResetCompletionState(); }
        rows.RemoveAll(value => value.ShopperSlotId == draft.ShopperSlotId &&
            DadRosterIdentity.SameAccount(value.ShopperAccountKey, draft.ShopperAccountKey) &&
            string.Equals(value.ShopperCharacterKey.Value, draft.ShopperCharacterKey.Value, StringComparison.OrdinalIgnoreCase));
        rows.Add(candidate);
        return TrySaveMemberAssignments(kind, ownerId, rows, out error);
    }
}
