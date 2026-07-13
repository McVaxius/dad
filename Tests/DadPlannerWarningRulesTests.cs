using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPlannerWarningRulesTests
{
    [Fact]
    public void PremadeWarningDoesNotMentionMogtome()
    {
        var request = new DadRunRequest
        {
            PremadeDuty = new DadPremadeDutyTask(),
        };

        var warnings = DadPlannerWarningRules.Build(request, new DadCharacterPool());

        var premade = Assert.Single(warnings, static warning => warning.Contains("Premade Duty", StringComparison.Ordinal));
        Assert.DoesNotContain("MOGTOME", premade, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MogtomeHasAccurateSoloHelperWarning()
    {
        var request = new DadRunRequest
        {
            Mogtome = new DadMogtomeTask(),
        };

        var warnings = DadPlannerWarningRules.Build(request, new DadCharacterPool());

        var mogtome = Assert.Single(warnings, static warning => warning.Contains("MOGTOME", StringComparison.Ordinal));
        Assert.Contains("solo DAD-owned helper IPC", mogtome, StringComparison.Ordinal);
        Assert.DoesNotContain("exact typed party workers", mogtome, StringComparison.OrdinalIgnoreCase);
    }
}
