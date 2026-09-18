using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadShoppingOrderTests
{
    private const string Row = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void AbsoluteProgressIsIdempotentAndInvalidatesOnlyChangedRows()
    {
        var other = Guid.NewGuid().ToString("D");
        Dictionary<string, long> first = new() { [Row] = 20, [other] = 5 };
        Dictionary<string, long> incoming = new() { [Row] = 40, [other] = 5 };
        var merged = DadShoppingAssociationRules.MergeCreditedQuantities(first, incoming)!;
        merged = DadShoppingAssociationRules.MergeCreditedQuantities(merged, incoming)!;
        Assert.Equal(40, merged[Row]);
        var replacement = Guid.NewGuid().ToString("D");
        merged = DadShoppingAssociationRules.MergeCreditedQuantities(merged, new Dictionary<string, long> { [Row] = 40, [replacement] = 0 })!;
        Assert.Equal(40, merged[Row]);
        Assert.DoesNotContain(other, merged.Keys);
        Assert.Equal(0, merged[replacement]);
        Assert.Equal(20, first[Row]);
    }

    [Fact]
    public void OptionalActionEditsPreserveProgressAndFrozenRunsAreIndependent()
    {
        var association = Association();
        var edited = association.Clone();
        edited.RunAutoRetainerDelivery = true;
        edited.CustomCommand = "/fixture";
        Assert.False(DadShoppingAssociationRules.ResetCompletionIfProvenanceChanged(association, edited));
        Assert.Equal(20, edited.CreditedQuantities![Row]);
        var plan = Plan(association);
        var frozen = DadShoppingAssociationRules.FreezePlan(plan)!;
        frozen.CreditedQuantities![Row] = 40;
        Assert.Equal(20, association.CreditedQuantities![Row]);
        var result = new DadShoppingRunResult { Association = frozen, CreditedQuantities = new() { [Row] = 40 } };
        var clone = result.Clone();
        clone.CreditedQuantities![Row] = 60;
        Assert.Equal(40, result.CreditedQuantities[Row]);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("character")]
    [InlineData("account")]
    [InlineData("order")]
    public void IdentityChangesResetOnlyTheAffectedOrder(string change)
    {
        var before = Association();
        var after = before.Clone();
        switch (change)
        {
            case "list": after.PresetId = Guid.NewGuid().ToString("D"); break;
            case "character": after.ShopperCharacterKey = new("fixture-b"); break;
            case "account": after.ShopperAccountKey = new("fixture-account-b"); break;
            case "order": after.AssociationId = Guid.NewGuid().ToString("N"); break;
        }
        Assert.True(DadShoppingAssociationRules.ResetCompletionIfProvenanceChanged(before, after));
        Assert.Null(after.CreditedQuantities);
        Assert.Equal(20, before.CreditedQuantities![Row]);
    }

    [Fact]
    public void ChangedSlotCannotPassExactShopperValidation()
    {
        var association = Association();
        var plan = Plan(association);
        var frozen = DadShoppingAssociationRules.FreezePlan(plan)!;
        Assert.True(DadShoppingAssociationRules.TryValidateForPlan(frozen, plan, out _));
        plan.Slots[0].RequiredCharacterKey = new("fixture-replacement");
        Assert.False(DadShoppingAssociationRules.TryValidateForPlan(frozen, plan, out _));
        Assert.Equal("fixture-a", frozen.ShopperCharacterKey.Value);
        Assert.Equal(20, frozen.CreditedQuantities![Row]);
    }

    [Fact]
    public void ScheduleShopperMustMatchEveryReferencedPlansExactPrimaryIdentity()
    {
        var source = Association();
        var first = Plan(source);
        var second = Plan(source.Clone());
        second.GroupId = "second-plan";
        var schedule = new DadScheduleDefinition
        {
            ShoppingAssociation = source.Clone(),
            Entries = [new() { GroupId = first.GroupId }, new() { GroupId = second.GroupId }],
        };
        var wizard = new DadShoppingWizardDraft();
        wizard.SelectDestination(DadShoppingAssociationOwnerKind.Schedule, schedule.ScheduleId, source);
        wizard.Next(); wizard.Next();
        DadAdsShopListPresetSummary[] lists = [new() { PresetId = source.PresetId }];
        Assert.True(wizard.Validate(true, lists,
            DadShoppingAssociationRules.ResolveCommonScheduleShopperSlots(schedule, [first, second]).ToArray(), out _));
        Assert.True(DadShoppingAssociationRules.TryValidateForSchedule(schedule, [first, second], out _));

        foreach (var change in new[] { "slot", "account", "character", "substitute", "missing-plan" })
        {
            second.Slots = Plan(source).Slots;
            switch (change)
            {
                case "slot":
                    second.Slots[0].SlotId = "Slot2";
                    second.Slots.Insert(0, new() { SlotId = "Slot1", RequiredAccountKey = new("other-account"), RequiredCharacterKey = new("other-character") });
                    break;
                case "account": second.Slots[0].RequiredAccountKey = new("changed-account"); break;
                case "character": second.Slots[0].RequiredCharacterKey = new("changed-character"); break;
                case "substitute": second.Slots[0].IsSubstitute = true; break;
                case "missing-plan": second.GroupId = "missing"; break;
            }
            var eligible = DadShoppingAssociationRules.ResolveCommonScheduleShopperSlots(schedule, [first, second]);
            Assert.False(wizard.Validate(true, lists, eligible.ToArray(), out _), change);
            Assert.False(DadShoppingAssociationRules.TryValidateForSchedule(schedule, [first, second], out _), change);
            Assert.Equal(source.ShopperCharacterKey, wizard.Association.ShopperCharacterKey);
            Assert.Equal(20, wizard.Association.CreditedQuantities![Row]);
        }
    }

    [Fact]
    public void MalformedProgressIsRejected()
    {
        Assert.False(DadShoppingAssociationRules.ValidCreditedQuantities(new Dictionary<string, long> { [Row] = -1 }));
        Assert.False(DadShoppingAssociationRules.ValidCreditedQuantities(new Dictionary<string, long> { ["bad"] = 2 }));
        Assert.True(DadShoppingAssociationRules.ValidCreditedQuantities(new Dictionary<string, long> { [Row] = 0 }));
    }

    [Fact]
    public void WizardNavigationAndCancellationNeverMutateSavedAssociations()
    {
        var source = Association();
        var wizard = new DadShoppingWizardDraft();
        wizard.SelectDestination(DadShoppingAssociationOwnerKind.Schedule, "exact-schedule", source);
        Assert.Equal("exact-schedule", wizard.DestinationId);
        Assert.Equal(DadShoppingAssociationOwnerKind.Schedule, wizard.DestinationKind);
        wizard.Association.CustomCommand = "/fixture";
        wizard.Association.CreditedQuantities![Row] = 40;
        wizard.Next(); wizard.Next(); wizard.Back();
        Assert.Equal(1, wizard.Step);
        Assert.Empty(source.CustomCommand);
        Assert.Equal(20, source.CreditedQuantities![Row]);
        wizard.Cancel();
        Assert.Empty(wizard.DestinationId);
        Assert.Empty(wizard.Association.PresetId);
        Assert.Null(wizard.Association.CreditedQuantities);
        Assert.Equal(20, source.CreditedQuantities![Row]);
    }

    [Fact]
    public void WizardDestinationChangesLoadThatDestinationAndClearStaleChoices()
    {
        var wizard = new DadShoppingWizardDraft();
        wizard.SelectDestination(DadShoppingAssociationOwnerKind.Plan, "first", Association());
        wizard.Association.CustomCommand = "/unsaved";
        wizard.Next();
        var second = Association();
        second.PresetId = Guid.NewGuid().ToString("D");
        wizard.SelectDestination(DadShoppingAssociationOwnerKind.Schedule, "second", second);
        Assert.Equal(second.PresetId, wizard.Association.PresetId);
        Assert.Empty(wizard.Association.CustomCommand);
        Assert.Equal(0, wizard.Step);
        wizard.SelectDestination(DadShoppingAssociationOwnerKind.Plan, "new", null);
        Assert.Empty(wizard.Association.PresetId);
        Assert.True(wizard.Association.ShopperCharacterKey.IsEmpty);
    }

    [Fact]
    public void WizardRejectsUnavailableListsChangedExactShoppersAndFailedSavesWithoutClosingDraft()
    {
        var source = Association();
        var wizard = new DadShoppingWizardDraft();
        wizard.SelectDestination(DadShoppingAssociationOwnerKind.Plan, "fixture-plan", source);
        var shoppers = Plan(source).Slots;
        DadAdsShopListPresetSummary[] lists = [new() { PresetId = source.PresetId, Name = "Fixture" }];
        Assert.True(wizard.Validate(true, lists, shoppers, out _));
        wizard.Next();
        Assert.False(wizard.Validate(true, [], shoppers, out _));
        Assert.False(wizard.Validate(false, lists, shoppers, out _));
        wizard.Next();
        shoppers[0].RequiredCharacterKey = new("changed");
        Assert.False(wizard.Validate(true, lists, shoppers, out _));
        Assert.Equal(source.ShopperCharacterKey, wizard.Association.ShopperCharacterKey);
        shoppers[0].RequiredCharacterKey = source.ShopperCharacterKey;
        Assert.True(wizard.Validate(true, lists, shoppers, out _));
        wizard.Next();
        foreach (var blocker in new[] { "Active work is locked", "Persistence failed" })
        {
            Assert.False(wizard.TrySave(_ => (false, blocker), out var message));
            Assert.Equal(blocker, message);
            Assert.Equal(3, wizard.Step);
            Assert.False(wizard.Saved);
            Assert.Equal(source.PresetId, wizard.Association.PresetId);
        }
        var saves = 0;
        Assert.True(wizard.TrySave(_ => { saves++; return (true, "persisted"); }, out _));
        Assert.False(wizard.TrySave(_ => { saves++; return (true, "persisted"); }, out _));
        Assert.Equal(1, saves);
    }

    private static DadShoppingAssociation Association() => new()
    {
        PresetId = "11111111-1111-1111-1111-111111111111", ShopperSlotId = "Slot1",
        ShopperAccountKey = new("fixture-account"), ShopperCharacterKey = new("fixture-a"),
        CreditedQuantities = new() { [Row] = 20 },
    };

    private static DadPlannerGroup Plan(DadShoppingAssociation association) => new()
    {
        GroupId = "fixture-plan", ShoppingAssociation = association,
        Slots = [new() { SlotId = "Slot1", RequiredAccountKey = association.ShopperAccountKey, RequiredCharacterKey = association.ShopperCharacterKey }],
    };
}
