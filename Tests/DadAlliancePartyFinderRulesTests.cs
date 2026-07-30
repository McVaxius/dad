using System.Text;
using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderRulesTests
{
    [Fact]
    public void SparsePresetWithRequiredAndOptionalAlliancesAndAllianceAHostIsValid()
    {
        var slots = new[]
        {
            Effective("Slot1", "Host Example@Alpha", 1001, DadAllianceAssignment.A),
            Effective("Slot2", "Second Example@Alpha", 1002, DadAllianceAssignment.A),
            Effective("Slot3", "Third Example@Beta", 1003, DadAllianceAssignment.B),
            Effective("Slot4", "Fourth Example@Gamma", 1004, DadAllianceAssignment.C),
            Effective("Slot5", "Fifth Example@Delta", 1005, DadAllianceAssignment.D),
            Effective("Slot6", "Sixth Example@Epsilon", 1006, DadAllianceAssignment.E),
            Effective("Slot7", "Seventh Example@Zeta", 1007, DadAllianceAssignment.F),
            Effective("Slot8", "Eighth Example@Eta", 1008, DadAllianceAssignment.G),
        };

        var validation = DadAlliancePartyFinderRules.ValidateEffectiveSlots(
            slots,
            new DadCharacterKey("Host Example@Alpha"));

        Assert.True(validation.IsValid, validation.Summary);
        Assert.Equal(2, validation.AllianceACount);
        Assert.Equal(1, validation.AllianceBCount);
        Assert.Equal(1, validation.AllianceCCount);
        Assert.Equal(1, validation.AllianceDCount);
        Assert.Equal(1, validation.AllianceECount);
        Assert.Equal(1, validation.AllianceFCount);
        Assert.Equal(1, validation.AllianceGCount);
        Assert.Equal(8, validation.TotalCount);
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
        Assert.Contains(validation.Blockers, static blocker => blocker.Contains("Assign A, B, C, D, E, F, or G", StringComparison.Ordinal));
        Assert.Contains(validation.Blockers, static blocker => blocker.Contains("Alliance A", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionalAllianceCapAndTotalCountAreEnforcedWithoutDynamicBalancing()
    {
        var rows = new[]
            {
                Saved("Slot1", DadAllianceAssignment.A),
                Saved("Slot2", DadAllianceAssignment.B),
                Saved("Slot3", DadAllianceAssignment.C),
            }
            .Concat(
                Enumerable.Range(4, 9)
                    .Select(index => Saved($"Slot{index}", DadAllianceAssignment.G)))
            .ToList();

        var validation = DadAlliancePartyFinderRules.ValidateSavedRows(rows);

        Assert.False(validation.IsValid);
        Assert.Equal(9, validation.AllianceGCount);
        Assert.Equal(12, validation.TotalCount);
        Assert.Contains(
            validation.Blockers,
            static blocker =>
                blocker.Contains("Alliance G", StringComparison.Ordinal) &&
                blocker.Contains("cannot exceed eight", StringComparison.Ordinal));
        Assert.False(DadAlliancePartyFinderRules.CanAssign(rows, "Slot1", DadAllianceAssignment.G));
        Assert.True(DadAlliancePartyFinderRules.CanAssign(rows, "Slot1", DadAllianceAssignment.None));
    }

    [Fact]
    public void MaximumTotalIncludesOptionalAllianceAssignments()
    {
        var rows = Enumerable.Range(1, 8)
            .Select(index => Saved($"A{index}", DadAllianceAssignment.A))
            .Concat(
                Enumerable.Range(1, 8)
                    .Select(index => Saved($"B{index}", DadAllianceAssignment.B)))
            .Concat(
                Enumerable.Range(1, 8)
                    .Select(index => Saved($"C{index}", DadAllianceAssignment.C)))
            .Append(Saved("G1", DadAllianceAssignment.G))
            .ToList();

        var validation = DadAlliancePartyFinderRules.ValidateSavedRows(rows);

        Assert.False(validation.IsValid);
        Assert.Equal(1, validation.AllianceGCount);
        Assert.Equal(25, validation.TotalCount);
        Assert.Contains(
            validation.Blockers,
            static blocker =>
                blocker.Contains("3-24", StringComparison.Ordinal) &&
                blocker.Contains("found 25", StringComparison.Ordinal));
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
        var validation = DadAlliancePartyFinderRules.ValidateSavedRows(normalized);
        Assert.True(validation.IsValid, validation.Summary);
        Assert.Equal(0, validation.AllianceDCount);
        Assert.Equal(0, validation.AllianceECount);
        Assert.Equal(0, validation.AllianceFCount);
        Assert.Equal(0, validation.AllianceGCount);
        Assert.Equal(3, validation.TotalCount);
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

    [Fact]
    public void PresetUiEnumerationPreservesNumericValuesAndIncludesAThroughG()
    {
        var assignments = Enum.GetValues<DadAllianceAssignment>();

        Assert.Equal(
            [
                DadAllianceAssignment.None,
                DadAllianceAssignment.A,
                DadAllianceAssignment.B,
                DadAllianceAssignment.C,
                DadAllianceAssignment.D,
                DadAllianceAssignment.E,
                DadAllianceAssignment.F,
                DadAllianceAssignment.G,
            ],
            assignments);
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7],
            assignments.Select(static assignment => (int)assignment).ToArray());
        Assert.False(DadAlliancePartyFinderRules.IsConcreteAssignment(DadAllianceAssignment.None));
        Assert.All(
            assignments.Skip(1),
            static assignment =>
                Assert.True(DadAlliancePartyFinderRules.IsConcreteAssignment(assignment)));
        Assert.False(
            DadAlliancePartyFinderRules.IsConcreteAssignment(
                (DadAllianceAssignment)8));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(6, 5)]
    [InlineData(7, 6)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    [InlineData(8, -1)]
    public void AllianceButtonsMapExactly(int rawAssignment, int expectedButton)
        => Assert.Equal(
            expectedButton,
            DadAlliancePartyFinderRules.GetJoinAllianceButtonIndex(
                (DadAllianceAssignment)rawAssignment));

    [Theory]
    [InlineData(-1, DadAllianceAssignment.None)]
    [InlineData(0, DadAllianceAssignment.A)]
    [InlineData(1, DadAllianceAssignment.B)]
    [InlineData(2, DadAllianceAssignment.C)]
    [InlineData(3, DadAllianceAssignment.D)]
    [InlineData(4, DadAllianceAssignment.E)]
    [InlineData(5, DadAllianceAssignment.F)]
    [InlineData(6, DadAllianceAssignment.G)]
    [InlineData(7, DadAllianceAssignment.None)]
    public void CrossRealmGroupsMapExactly(int groupIndex, DadAllianceAssignment expected)
        => Assert.Equal(expected, DadAlliancePartyFinderRules.FromCrossRealmGroupIndex(groupIndex));

    [Fact]
    public void StatusClonePreservesEveryAllianceCount()
    {
        var status = new DadAlliancePartyFinderStatus
        {
            Validation = new DadAlliancePresetValidation
            {
                AllianceACount = 1,
                AllianceBCount = 2,
                AllianceCCount = 3,
                AllianceDCount = 4,
                AllianceECount = 5,
                AllianceFCount = 6,
                AllianceGCount = 7,
            },
        };

        var clone = status.Clone();

        Assert.Equal(1, clone.Validation.AllianceACount);
        Assert.Equal(2, clone.Validation.AllianceBCount);
        Assert.Equal(3, clone.Validation.AllianceCCount);
        Assert.Equal(4, clone.Validation.AllianceDCount);
        Assert.Equal(5, clone.Validation.AllianceECount);
        Assert.Equal(6, clone.Validation.AllianceFCount);
        Assert.Equal(7, clone.Validation.AllianceGCount);
        Assert.Equal(28, clone.Validation.TotalCount);
    }

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

    [Fact]
    public void LookingForGroupDiagnosticsUseUniqueUtf8WholeFiles()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "dad-alliance-diagnostics",
            Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp =
                new DateTime(2026, 7, 29, 23, 45, 6, DateTimeKind.Utc);
            var audit = new DadAlliancePfAuditLog(root);
            const string first = "standard tree: Recruiter Ω\r\nnode[0]";
            const string second = "compact tree: 二番目\nnode[1]";

            Assert.True(
                audit.TryWriteLookingForGroupDiagnostics(
                    first,
                    timestamp,
                    out var firstPath,
                    out var firstError),
                firstError);
            Assert.True(
                audit.TryWriteLookingForGroupDiagnostics(
                    second,
                    timestamp,
                    out var secondPath,
                    out var secondError),
                secondError);

            Assert.NotEqual(firstPath, secondPath);
            Assert.EndsWith(
                Path.Combine(
                    "alliance-pf",
                    "diagnostics",
                    "looking-for-group-tree-20260729T234506.0000000Z.txt"),
                firstPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                Path.Combine(
                    "alliance-pf",
                    "diagnostics",
                    "looking-for-group-tree-20260729T234506.0000000Z-001.txt"),
                secondPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(first, File.ReadAllText(firstPath, Encoding.UTF8));
            Assert.Equal(second, File.ReadAllText(secondPath, Encoding.UTF8));
            Assert.Equal(
                new UTF8Encoding(false).GetBytes(first),
                File.ReadAllBytes(firstPath));
            Assert.Equal(
                new UTF8Encoding(false).GetBytes(second),
                File.ReadAllBytes(secondPath));
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
