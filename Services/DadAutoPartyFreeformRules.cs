using dad.Models;

namespace dad.Services;

internal static class DadAutoPartyFreeformRules
{
    internal const int MaximumParticipants = 8;
    internal const string GroupIdPrefix = "autoparty-freeform-";
    internal const string FormationActivityId = "dad-formation";

    public static bool TryBuild(
        IReadOnlyList<DadAutoPartyFreeformParticipant> participants,
        out DadAutoPartyFreeformFormation formation,
        out string blocker)
    {
        formation = new DadAutoPartyFreeformFormation(new DadPlannerGroup(), []);
        blocker = string.Empty;
        participants ??= [];
        if (participants.Count is < 2 or > MaximumParticipants)
            return Fail("AutoParty Create party requires two to eight selected characters.", out blocker);
        if (participants.Any(static participant => participant == null))
        {
            return Fail("AutoParty Create party selections must be non-empty and unique.", out blocker);
        }
        var selectionKeys = participants
            .Select(static participant => Normalize(participant.SelectionKey))
            .ToList();
        if (selectionKeys.Any(static key => key.Length == 0) ||
            selectionKeys.Distinct(StringComparer.Ordinal).Count() != participants.Count)
            return Fail("AutoParty Create party selections must be non-empty and unique.", out blocker);

        var groupId = $"{GroupIdPrefix}{Guid.NewGuid():N}";
        var slots = new List<DadPlannerGroupSlot>(participants.Count);
        var bindings = new List<DadAutoPartyRemoteBinding>();
        var localAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localCharacters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteCharacters = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            var slotId = DadPlannerSlotRules.FormatSlotId(index + 1);
            if (!DadRosterCharacterMerge.IsCombatJob(participant.RequestedJobId))
                return Fail($"{slotId} requires one permitted combat job.", out blocker);

            var slot = new DadPlannerGroupSlot
            {
                SlotId = slotId,
                RequiredRole = DadPartyRole.Any,
                RequiredJobId = participant.RequestedJobId,
                WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly,
                CharacterLoadInstruction = new DadCharacterLoadInstruction
                {
                    Enabled = false,
                    DryRun = true,
                },
                AllowSubstitution = false,
            };
            if (participant.Kind == DadAutoPartyFreeformParticipantKind.Local)
            {
                var account = participant.AccountKey.Value.Trim();
                var character = participant.CharacterKey.Value.Trim();
                if (account.Length == 0 || character.Length == 0 || participant.ContentId == 0 ||
                    !localAccounts.Add(account) || !localCharacters.Add(character) ||
                    Normalize(participant.OwnerId).Length > 0 || Normalize(participant.IslandId).Length > 0 ||
                    Normalize(participant.OpaqueCharacterId).Length > 0)
                {
                    return Fail($"{slotId} has an invalid or duplicate local character route.", out blocker);
                }
                slot.RequiredAccountKey = new DadAccountKey(account);
                slot.RequiredCharacterKey = new DadCharacterKey(character);
            }
            else if (participant.Kind == DadAutoPartyFreeformParticipantKind.RegisteredIsland)
            {
                var ownerId = DadAutoPartyConfiguration.NormalizeIdentifier(participant.OwnerId);
                var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(participant.IslandId);
                var opaqueCharacterId = DadAutoPartyConfiguration.NormalizeIdentifier(participant.OpaqueCharacterId);
                var routeKey = $"{ownerId}\n{islandId}\n{opaqueCharacterId}";
                if (!participant.AccountKey.IsEmpty || !participant.CharacterKey.IsEmpty || participant.ContentId != 0 ||
                    ownerId.Length == 0 || islandId.Length == 0 || opaqueCharacterId.Length == 0 ||
                    !remoteCharacters.Add(routeKey))
                {
                    return Fail($"{slotId} has an invalid or duplicate registered-island route.", out blocker);
                }
                slot.SharedIdentity = new DadSharedIdentityPlaceholder
                {
                    IdentityToken = opaqueCharacterId,
                    CharacterLabel = LimitLabel(participant.DisplayLabel, slotId),
                    RequiresCharacter = true,
                };
                bindings.Add(new DadAutoPartyRemoteBinding
                {
                    FleetRowId = $"{groupId}-{slotId.ToLowerInvariant()}",
                    OpaqueCharacterId = opaqueCharacterId,
                    OwnerId = ownerId,
                    IslandId = islandId,
                    RequestedJobId = participant.RequestedJobId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    OwnsQueueAuthority = index == 0,
                    OwnerConsentConfirmed = true,
                });
            }
            else
            {
                return Fail($"{slotId} has an unsupported AutoParty route.", out blocker);
            }
            slots.Add(slot);
        }

        formation = new DadAutoPartyFreeformFormation(
            new DadPlannerGroup
            {
                GroupId = groupId,
                DisplayName = "AutoParty freeform party",
                RunFamily = DadPlannerRunFamily.DutyFinder,
                ActivityMode = DadPlannerActivityMode.PremadeDuty,
                OperatorMode = DadPlannerOperatorMode.RemotePartyPlan,
                ConnectedOnly = true,
                SameDatacenterOnly = false,
                AllowStaleForPlanning = false,
                TransportOwner = DadTransportOwner.DadDirect,
                QueueAuthority = DadQueueAuthority.Leader,
                InviteAuthority = DadInviteAuthority.PresetLeader,
                DutyExpectedPartySize = slots.Count,
                AutoPartyProposalId = string.Empty,
                AutoPartyFormationOnly = true,
                Slots = slots,
                IsTemplate = false,
                ScheduleEnabled = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            },
            bindings);
        return true;
    }

    public static bool IsFreeformGroupId(string? groupId)
        => (groupId ?? string.Empty).StartsWith(GroupIdPrefix, StringComparison.Ordinal);

    private static string LimitLabel(string? label, string fallback)
    {
        var normalized = (label ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return fallback;
        return normalized.Length <= 96 ? normalized : normalized[..96];
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static bool Fail(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }
}

/// <summary>
/// Holds only the opaque registered-island routes selected for the current freeform formation.
/// It is a runtime service, not configuration state, so these bindings cannot be serialized into
/// Plans, Schedules, Fleet Matrix, IPC, or the plugin configuration.
/// </summary>
internal sealed class DadAutoPartyRuntimeBindingStore
{
    private readonly object gate = new();
    private string stagedGroupId = string.Empty;
    private List<DadAutoPartyRemoteBinding> stagedBindings = [];

    public string StagedGroupId
    {
        get
        {
            lock (gate)
                return stagedGroupId;
        }
    }

    public bool TryStage(DadAutoPartyFreeformFormation formation, out string blocker)
    {
        ArgumentNullException.ThrowIfNull(formation);
        blocker = string.Empty;
        if (!DadAutoPartyFreeformRules.IsFreeformGroupId(formation.Group.GroupId))
            return Fail("Only an in-memory AutoParty freeform group may stage runtime bindings.", out blocker);

        var remoteTokens = formation.Group.Slots
            .Select(static slot => slot.SharedIdentity?.IdentityToken?.Trim() ?? string.Empty)
            .Where(static token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var bindings = (formation.RemoteBindings ?? [])
            .Where(static binding => binding != null)
            .Select(static binding => binding.Clone().Normalize())
            .ToList();
        if (bindings.Count != remoteTokens.Count || bindings.Any(binding =>
                !binding.IsValid || !remoteTokens.Contains(binding.OpaqueCharacterId)) ||
            bindings.Select(static binding => binding.OpaqueCharacterId)
                .Distinct(StringComparer.Ordinal).Count() != bindings.Count)
        {
            return Fail("AutoParty freeform runtime bindings do not match the selected opaque roster.", out blocker);
        }

        var slotOneToken = formation.Group.Slots
            .OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.SlotId))
            .FirstOrDefault()?.SharedIdentity?.IdentityToken?.Trim() ?? string.Empty;
        if (bindings.Any(binding => binding.OwnsQueueAuthority !=
                string.Equals(binding.OpaqueCharacterId, slotOneToken, StringComparison.Ordinal)))
        {
            return Fail("AutoParty freeform runtime bindings do not preserve Slot 1 queue authority.", out blocker);
        }

        lock (gate)
        {
            stagedGroupId = formation.Group.GroupId.Trim();
            stagedBindings = bindings;
        }
        return true;
    }

    public IReadOnlyList<DadAutoPartyRemoteBinding> Snapshot(
        IReadOnlyList<DadAutoPartyRemoteBinding>? durableBindings)
    {
        lock (gate)
        {
            var stagedTokens = stagedBindings
                .Select(static binding => binding.OpaqueCharacterId)
                .ToHashSet(StringComparer.Ordinal);
            return stagedBindings
                .Concat((durableBindings ?? [])
                    .Where(static binding => binding != null)
                    .Where(binding => !stagedTokens.Contains(binding.OpaqueCharacterId)))
                .Select(static binding => binding.Clone().Normalize())
                .Where(static binding => binding.IsValid)
                .Take(256)
                .ToList();
        }
    }

    public bool Clear(string? expectedGroupId = null)
    {
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(expectedGroupId) &&
                !string.Equals(stagedGroupId, expectedGroupId.Trim(), StringComparison.Ordinal))
                return false;
            var changed = stagedGroupId.Length > 0 || stagedBindings.Count > 0;
            stagedGroupId = string.Empty;
            stagedBindings = [];
            return changed;
        }
    }

    private static bool Fail(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }
}
