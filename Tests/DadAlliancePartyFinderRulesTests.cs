using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderRulesTests
{
    [Fact]
    public void SparsePresetWithEveryAllianceAndAllianceAHostIsValid()
    {
        var slots = new[]
        {
            Effective("Slot1", "Host Example@Alpha", 1001, DadAllianceAssignment.A),
            Effective("Slot2", "Second Example@Alpha", 1002, DadAllianceAssignment.A),
            Effective("Slot3", "Third Example@Beta", 1003, DadAllianceAssignment.B),
            Effective("Slot4", "Fourth Example@Gamma", 1004, DadAllianceAssignment.C),
        };

        var validation = DadAlliancePartyFinderRules.ValidateEffectiveSlots(
            slots,
            new DadCharacterKey("Host Example@Alpha"));

        Assert.True(validation.IsValid, validation.Summary);
        Assert.Equal(2, validation.AllianceACount);
        Assert.Equal(1, validation.AllianceBCount);
        Assert.Equal(1, validation.AllianceCCount);
        Assert.Equal(4, validation.TotalCount);
    }

    [Fact]
    public void MissingAssignmentAndNonAllianceAHostAreVisibleBlockers()
    {
        var slots = new[]
        {
            Effective("Slot1", "Host Example@Alpha", 1001, DadAllianceAssignment.B),
            Effective("Slot2", "Second Example@Beta", 1002, DadAllianceAssignment.None),
            Effective("Slot3", "Third Example@Gamma", 1003, DadAllianceAssignment.C),
        };

        var validation = DadAlliancePartyFinderRules.ValidateEffectiveSlots(
            slots,
            new DadCharacterKey("Host Example@Alpha"));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Blockers, static blocker => blocker.Contains("Assign A, B, or C", StringComparison.Ordinal));
        Assert.Contains(validation.Blockers, static blocker => blocker.Contains("Alliance A", StringComparison.Ordinal));
    }

    [Fact]
    public void AllianceCapAndTotalCapAreEnforcedWithoutDynamicBalancing()
    {
        var rows = Enumerable.Range(1, 9)
            .Select(index => Saved($"Slot{index}", DadAllianceAssignment.A))
            .Append(Saved("Slot10", DadAllianceAssignment.B))
            .Append(Saved("Slot11", DadAllianceAssignment.C))
            .ToList();

        var validation = DadAlliancePartyFinderRules.ValidateSavedRows(rows);

        Assert.False(validation.IsValid);
        Assert.Equal(9, validation.AllianceACount);
        Assert.Contains(validation.Blockers, static blocker => blocker.Contains("cannot exceed eight", StringComparison.Ordinal));
        Assert.False(DadAlliancePartyFinderRules.CanAssign(rows, "Slot10", DadAllianceAssignment.A));
        Assert.True(DadAlliancePartyFinderRules.CanAssign(rows, "Slot10", DadAllianceAssignment.None));
    }

    [Fact]
    public void SubstituteRowsAlwaysInheritThePrimaryAlliance()
    {
        var normalized = DadPlannerSlotRules.NormalizeGroupSlots(
        [
            Saved("Slot1", DadAllianceAssignment.A),
            new DadPlannerGroupSlot
            {
                SlotId = "Slot1",
                IsSubstitute = true,
                AllianceAssignment = DadAllianceAssignment.C,
            },
            Saved("Slot2", DadAllianceAssignment.B),
            Saved("Slot3", DadAllianceAssignment.C),
        ]);

        Assert.Equal(DadAllianceAssignment.A, normalized[0].AllianceAssignment);
        Assert.True(normalized[1].IsSubstitute);
        Assert.Equal(DadAllianceAssignment.A, normalized[1].AllianceAssignment);
        Assert.True(DadAlliancePartyFinderRules.ValidateSavedRows(normalized).IsValid);
    }

    [Fact]
    public void PasscodeUsesExactCryptographicBoundsAndInjectableValue()
    {
        var called = false;

        var passcode = DadAlliancePartyFinderRules.GeneratePasscode((minimum, maximumExclusive) =>
        {
            called = true;
            Assert.Equal(1000, minimum);
            Assert.Equal(10000, maximumExclusive);
            return 4321;
        });

        Assert.True(called);
        Assert.Equal(4321, passcode);
        for (var sample = 0; sample < 100; sample++)
            Assert.InRange(DadAlliancePartyFinderRules.GeneratePasscode(), 1000, 9999);
    }

    [Fact]
    public void ExactLeaderAndWorldMatchingIsCaseInsensitiveAndCommentIndependent()
    {
        const string arbitraryListingComment = "This text is deliberately unrelated to DAD.";

        Assert.True(DadAlliancePartyFinderRules.IsExactListingMatch(
            "  Host Example ",
            " ALPHA ",
            "host example",
            "Alpha"));
        Assert.False(DadAlliancePartyFinderRules.IsExactListingMatch(
            "Host Example",
            "Beta",
            "Host Example",
            "Alpha"));
        Assert.NotEmpty(arbitraryListingComment);
    }

    [Theory]
    [InlineData(DadAllianceAssignment.A, 0)]
    [InlineData(DadAllianceAssignment.B, 1)]
    [InlineData(DadAllianceAssignment.C, 2)]
    [InlineData(DadAllianceAssignment.None, -1)]
    public void AllianceButtonsMapExactly(DadAllianceAssignment assignment, int expectedButton)
        => Assert.Equal(expectedButton, DadAlliancePartyFinderRules.GetJoinAllianceButtonIndex(assignment));

    [Theory]
    [InlineData(0, DadAllianceAssignment.A)]
    [InlineData(1, DadAllianceAssignment.B)]
    [InlineData(2, DadAllianceAssignment.C)]
    [InlineData(3, DadAllianceAssignment.None)]
    public void CrossRealmGroupsMapExactly(int groupIndex, DadAllianceAssignment expected)
        => Assert.Equal(expected, DadAlliancePartyFinderRules.FromCrossRealmGroupIndex(groupIndex));

    [Fact]
    public void RetryBackoffCapsAtFifteenSecondsWithoutAttemptLimit()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DadAlliancePartyFinderRules.GetRetryDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(4), DadAlliancePartyFinderRules.GetRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(8), DadAlliancePartyFinderRules.GetRetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(15), DadAlliancePartyFinderRules.GetRetryDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(15), DadAlliancePartyFinderRules.GetRetryDelay(1_000_000));
    }

    [Theory]
    [InlineData(DadAllianceRecruitmentState.CreatingListing, false, 0, "ApplyPreset")]
    [InlineData(DadAllianceRecruitmentState.Blocked, false, 0, "Blocked")]
    [InlineData(DadAllianceRecruitmentState.ListingOpen, true, 0, "Complete")]
    [InlineData(DadAllianceRecruitmentState.ListingOpen, true, 777, "Complete")]
    public void StopControlIsAvailableForPendingBlockedAndPublishedOwnedRecruitment(
        DadAllianceRecruitmentState state,
        bool ownsRecruitment,
        ulong listingId,
        string createStage)
    {
        var status = new DadAlliancePartyFinderStatus
        {
            State = state,
            OwnsRecruitment = ownsRecruitment,
            ListingId = listingId,
            CreateStage = createStage,
        };

        Assert.True(DadAlliancePartyFinderRules.CanStop(status));
    }

    [Fact]
    public void StopControlRejectsIdleAndUnownedPublishedState()
    {
        Assert.False(DadAlliancePartyFinderRules.CanStop(new DadAlliancePartyFinderStatus()));
        Assert.False(DadAlliancePartyFinderRules.CanStop(
            new DadAlliancePartyFinderStatus
            {
                State = DadAllianceRecruitmentState.ListingOpen,
                ListingId = 777,
                OwnsRecruitment = false,
                CreateStage = "Complete",
            }));
    }

    [Theory]
    [InlineData(DadAllianceRecruitmentState.ListingOpen, true, 777ul, true)]
    [InlineData(DadAllianceRecruitmentState.ListingOpen, true, 0ul, true)]
    [InlineData(DadAllianceRecruitmentState.ListingOpen, true, ulong.MaxValue, true)]
    [InlineData(DadAllianceRecruitmentState.ListingOpen, false, 0ul, false)]
    [InlineData(DadAllianceRecruitmentState.ListingOpen, false, 777ul, false)]
    [InlineData(DadAllianceRecruitmentState.Blocked, true, 777ul, false)]
    [InlineData(DadAllianceRecruitmentState.Stopped, true, 777ul, false)]
    public void GrabRequiresOwnedListingOpenRegardlessOfDiagnosticOwnerHandle(
        DadAllianceRecruitmentState state,
        bool ownsRecruitment,
        ulong listingId,
        bool expected)
    {
        var status = new DadAlliancePartyFinderStatus
        {
            State = state,
            OwnsRecruitment = ownsRecruitment,
            ListingId = listingId,
        };

        Assert.Equal(
            expected,
            DadAlliancePartyFinderRules.CanGrabDads(status));
    }

    [Fact]
    public void HubAndDiscordCopiesDeduplicateByRecruitmentAndTarget()
    {
        var dedupe = new DadAllianceDeliveryDedupe();
        var recruitmentId = Guid.NewGuid().ToString("N");
        var target = new DadCharacterKey("Target Example@Beta");

        Assert.True(dedupe.TryAccept(recruitmentId, target, 4));
        Assert.False(dedupe.TryAccept(recruitmentId, target, 4));
        Assert.True(dedupe.TryAccept(recruitmentId, target, 5));
        Assert.True(dedupe.TryAccept(recruitmentId, new DadCharacterKey("Other Example@Beta"), 4));
        dedupe.Reset(recruitmentId, target);
        Assert.True(dedupe.TryAccept(recruitmentId, target, 4));
    }

    [Fact]
    public void UiSnapshotSerializationContainsNoPasscodeOrTransportSecrets()
    {
        var snapshot = new DadAlliancePfUiSnapshotDto
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            WorkerSessionId = new DadWorkerSessionId("worker-fixture"),
            TargetCharacterKey = new DadCharacterKey("Target Example@Beta"),
            AssignedAlliance = DadAllianceAssignment.B,
            ObservedAlliance = DadAllianceAssignment.B,
            Attempt = 3,
            State = DadAllianceRecruitmentState.Complete,
            StopGeneration = 2,
            SafeStatusCode = "dad-alliance-verified",
        };

        var json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("passcode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createStage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nextRetry", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastError", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalAuditIsAppendOnlyAndRecordsTheExactGeneratedPasscode()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-alliance-audit", Guid.NewGuid().ToString("N"));
        try
        {
            var passcode = DadAlliancePartyFinderRules.GeneratePasscode((_, _) => 6789);
            var timestamp = new DateTime(2026, 7, 24, 2, 3, 4, DateTimeKind.Utc);
            var audit = new DadAlliancePfAuditLog(root);
            var record = new DadAlliancePfAuditRecord
            {
                TimestampUtc = timestamp,
                Event = "listing-open",
                RecruitmentId = Guid.NewGuid().ToString("N"),
                HostName = "Host Example",
                HostWorld = "Alpha",
                TargetName = "Target Example",
                TargetWorld = "Beta",
                ExpectedAlliance = DadAllianceAssignment.B,
                Passcode = passcode,
                Attempt = 3,
                CreateStage = DadAlliancePfCreateStage.Submit.ToString(),
                NextRetryUtc = timestamp.AddSeconds(8),
                LastError = "synthetic PF error toast",
                Readiness = "main-ready=true; condition-ready=true",
                Category = DadAlliancePartyFinderCreateFlow.RaidsCategoryMask,
                DutyId = 174,
                PfOwnerHandle = 3,
                ActiveRecruitment = false,
                EditorVisible = true,
                SubmitDispatched = false,
                ConfigurationTarget = string.Empty,
                ObservedSettings =
                    "alliance-tab=False; alliance-a=False; " +
                    "passcode-visible=9752; passcode-stored=9752",
                Summary =
                    "Loaded the DAD-owned Alliance preset and invoked one refresh.",
                Evidence = new Dictionary<string, string>
                {
                    ["condition-66-using-party-finder"] = "true",
                    ["condition-84-participating-cross-world-party-or-alliance"] =
                        "true",
                },
            };

            Assert.True(audit.TryWrite(record));
            record.Event = "verified";
            Assert.True(audit.TryWrite(record));

            var path = audit.GetPath(timestamp);
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, static line => Assert.Contains("\"passcode\":6789", line, StringComparison.Ordinal));
            Assert.Contains("\"hostName\":\"Host Example\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"createStage\":\"Submit\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"lastError\":\"synthetic PF error toast\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"category\":32", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"dutyId\":174", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"pfOwnerHandle\":3", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"activeRecruitment\":false", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"editorVisible\":true", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"submitDispatched\":false", lines[0], StringComparison.Ordinal);
            Assert.Contains(
                "\"condition-66-using-party-finder\":\"true\"",
                lines[0],
                StringComparison.Ordinal);
            Assert.Contains(
                "\"condition-84-participating-cross-world-party-or-alliance\":\"true\"",
                lines[0],
                StringComparison.Ordinal);
            Assert.Contains("\"configurationTarget\":\"\"", lines[0], StringComparison.Ordinal);
            Assert.Contains(
                "\"observedSettings\":\"alliance-tab=False; alliance-a=False; " +
                "passcode-visible=9752; passcode-stored=9752\"",
                lines[0],
                StringComparison.Ordinal);
            Assert.Contains(
                "Loaded the DAD-owned Alliance preset and invoked one refresh.",
                lines[0],
                StringComparison.Ordinal);
            Assert.EndsWith(
                Path.Combine("alliance-pf", "logs", "alliance-pf-20260724.jsonl"),
                path,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static DadPlannerGroupSlot Saved(string slotId, DadAllianceAssignment assignment)
        => new()
        {
            SlotId = slotId,
            AllianceAssignment = assignment,
        };

    private static DadPresetCharacterSlot Effective(
        string slotId,
        string characterKey,
        ulong contentId,
        DadAllianceAssignment assignment)
        => new()
        {
            SlotId = slotId,
            CharacterKey = characterKey,
            RequiredCharacterKey = new DadCharacterKey(characterKey),
            ContentId = contentId,
            AllianceAssignment = assignment,
        };
}
