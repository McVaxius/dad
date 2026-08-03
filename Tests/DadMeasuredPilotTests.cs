using System.Security.Cryptography;
using System.Text.Json;
using AutoParty.Core.Cryptography;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadMeasuredPilotTests
{
    [Fact]
    public void EvaluationRequiresEveryMeasuredCoverageDimension()
    {
        var campaign = PassingCampaign();

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.True(evaluation.Passed);
        Assert.Equal(10, evaluation.QualifyingSuccesses);
        Assert.Equal(3, evaluation.PlanSuccesses);
        Assert.Equal(3, evaluation.ScheduleSuccesses);
        Assert.Equal(2, evaluation.RequestedJobSuccesses);
        Assert.Equal(1, evaluation.RequestedJobSwitches);
        Assert.Empty(evaluation.Missing);
    }

    [Fact]
    public void FailuresRemainInCampaignButDoNotCountTowardTen()
    {
        var campaign = PassingCampaign();
        var failed = QualifyingRun("failed", DadMeasuredPilotOrigin.Plans);
        failed.Successful = false;
        failed.FailureCode = "ordinary-failure";
        campaign.Runs.Add(failed);

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.Equal(11, campaign.Runs.Count);
        Assert.Equal(10, evaluation.QualifyingSuccesses);

    }

    [Fact]
    public void SafetyViolationHardFailsOtherwisePassingCampaign()
    {
        var campaign = PassingCampaign();
        campaign.SafetyViolations.Add("queue-before-ready");

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.Equal(DadMeasuredPilotState.HardFailed, evaluation.State);
        Assert.False(evaluation.Passed);
    }

    [Fact]
    public void IncompleteEvaluationReportsExactMissingCountsAndCanBeResumed()
    {
        var campaign = new DadMeasuredPilotCampaign
        {
            State = DadMeasuredPilotState.Active,
            StoppedAtUtc = DateTime.UtcNow,
            Runs = [QualifyingRun("one", DadMeasuredPilotOrigin.Plans)],
        };

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.Equal(DadMeasuredPilotState.EvaluationIncomplete, evaluation.State);
        Assert.Contains("successful multi-client executions: 1/10", evaluation.Missing);
        Assert.Contains("direct Plans executions: 1/3", evaluation.Missing);
        Assert.Contains("Schedule executions: 0/3", evaluation.Missing);
    }

    [Fact]
    public void NewCampaignDoesNotInheritDiscordOrStopObservations()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-measured-pilot-reset", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyPath = Path.Combine(root, "dad-test.dll");
        File.WriteAllBytes(assemblyPath, [1, 2, 3]);
        var fixture = SigningFixture(root);
        try
        {
            fixture.Configuration.MeasuredPilot.State = DadMeasuredPilotState.Active;
            fixture.Configuration.MeasuredPilot.StopAllVerified = true;
            fixture.Configuration.MeasuredPilot.RecoveryRunVerified = true;
            var service = new DadMeasuredPilotService(
                fixture.Configuration,
                fixture.Signing,
                static () => true,
                static () => { },
                assemblyPath);
            service.ObserveDiscordHealth(Health(DadAutoPartyDiscordConnectionState.Ready));
            service.ObserveDiscordHealth(Health(DadAutoPartyDiscordConnectionState.Disconnected));
            fixture.Configuration.MeasuredPilot.State = DadMeasuredPilotState.EvaluationIncomplete;

            var started = service.Start();
            service.ObserveDiscordHealth(Health(DadAutoPartyDiscordConnectionState.Ready));

            Assert.True(started.Allowed, started.SafeCode);
            Assert.False(fixture.Configuration.MeasuredPilot.DiscordReconnectCycleVerified);
            Assert.False(fixture.Configuration.MeasuredPilot.StopAllVerified);
            Assert.False(fixture.Configuration.MeasuredPilot.RecoveryRunVerified);
        }
        finally
        {
            fixture.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReceiptWriteFailureRestoresDirtyStateAndSchedulesRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-measured-pilot-retry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyPath = Path.Combine(root, "dad-test.dll");
        File.WriteAllBytes(assemblyPath, [1, 2, 3]);
        File.WriteAllText(Path.Combine(root, "pilot-receipts"), "blocks receipt directory creation");
        var fixture = SigningFixture(root);
        var diagnostics = new List<string>();
        try
        {
            var service = new DadMeasuredPilotService(
                fixture.Configuration,
                fixture.Signing,
                static () => true,
                static () => { },
                assemblyPath,
                diagnostics.Add);
            Assert.True(service.Start().Allowed);

            service.Update();
            for (var attempt = 0; attempt < 2000 && diagnostics.Count == 0; attempt++)
            {
                await Task.Yield();
                service.Update();
            }

            Assert.Contains("dad-pilot-receipt-write-failed", diagnostics);
            Assert.True(service.ReceiptDirty);
            Assert.False(service.ReceiptWriteInFlight);
            Assert.True(service.NextReceiptAttemptUtc > DateTime.UtcNow);
            Assert.Equal(string.Empty, fixture.Configuration.MeasuredPilot.ReceiptPath);
        }
        finally
        {
            fixture.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SuccessfulReceiptMutatesAndSavesOnlyFromUpdateCallerThread()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-measured-pilot-thread", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyPath = Path.Combine(root, "dad-test.dll");
        File.WriteAllBytes(assemblyPath, [1, 2, 3]);
        var fixture = SigningFixture(root);
        var frameworkThreadId = Environment.CurrentManagedThreadId;
        var saveThreadIds = new List<int>();
        try
        {
            var service = new DadMeasuredPilotService(
                fixture.Configuration,
                fixture.Signing,
                static () => true,
                () => saveThreadIds.Add(Environment.CurrentManagedThreadId),
                assemblyPath);
            Assert.True(service.Start().Allowed);

            for (var attempt = 0;
                 attempt < 20_000 && string.IsNullOrWhiteSpace(fixture.Configuration.MeasuredPilot.ReceiptPath);
                 attempt++)
            {
                service.Update();
                Thread.Yield();
            }

            Assert.False(string.IsNullOrWhiteSpace(fixture.Configuration.MeasuredPilot.ReceiptPath));
            Assert.NotEmpty(saveThreadIds);
            Assert.All(saveThreadIds, threadId => Assert.Equal(frameworkThreadId, threadId));
        }
        finally
        {
            fixture.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static DadMeasuredPilotCampaign PassingCampaign()
    {
        var runs = Enumerable.Range(0, 10).Select(index =>
        {
            var origin = index < 3 ? DadMeasuredPilotOrigin.Plans :
                index < 6 ? DadMeasuredPilotOrigin.Schedules : DadMeasuredPilotOrigin.Unknown;
            var run = QualifyingRun($"run-{index}", origin);
            if (index < 2)
            {
                run.RequestedJobRun = true;
                run.RequestedJobMatched = true;
                run.RequestedJobSwitched = index == 0;
            }
            return run;
        }).ToList();
        return new DadMeasuredPilotCampaign
        {
            State = DadMeasuredPilotState.Active,
            StoppedAtUtc = DateTime.UtcNow,
            Runs = runs,
            StopAllVerified = true,
            RecoveryRunVerified = true,
            DiscordReconnectCycleVerified = true,
            RevokeExclusionVerified = true,
            RePairVerified = true,
        };
    }

    private static DadMeasuredPilotRunEvidence QualifyingRun(string id, DadMeasuredPilotOrigin origin) => new()
    {
        RunId = id,
        Origin = origin,
        Terminal = true,
        Successful = true,
        ParticipantCount = 2,
        HealthyApplicationIds = [10, 20],
        FormationVerified = true,
        ReadinessBeforeQueueVerified = true,
        LeaseCleanupVerified = true,
        ClaimCleanupVerified = true,
        SchedulerCleanupVerified = true,
        ProfileRestoration = "not-applicable",
    };

    private static DadAutoPartyDiscordHealth Health(DadAutoPartyDiscordConnectionState state)
        => new(state, state.ToString(), DateTime.UtcNow, DateTime.UtcNow, 1, 2, true);

    private static SigningContext SigningFixture(string pilotExchangeRoot)
    {
        var signingPrivate = RandomNumberGenerator.GetBytes(32);
        var encryptionPrivate = RandomNumberGenerator.GetBytes(32);
        var signingPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(signingPrivate);
        var package = JsonSerializer.SerializeToUtf8Bytes(new DadAutoPartyPrivateIdentityPackage(
            "owner-test",
            "island-test",
            1,
            Convert.ToBase64String(signingPrivate),
            Convert.ToBase64String(encryptionPrivate)));
        var configuration = new DadAutoPartyConfiguration
        {
            PilotExchangeRoot = pilotExchangeRoot,
            EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RegisteredOwnerId = "owner-test",
            RegisteredIslandId = "island-test",
            SigningPublicKey = Convert.ToBase64String(signingPublic),
        };
        CryptographicOperations.ZeroMemory(signingPrivate);
        CryptographicOperations.ZeroMemory(encryptionPrivate);
        CryptographicOperations.ZeroMemory(signingPublic);
        var store = new MemoryIdentityStore(package);
        return new(configuration, new DadAutoPartySigningService(configuration, store), store);
    }

    private sealed record SigningContext(
        DadAutoPartyConfiguration Configuration,
        DadAutoPartySigningService Signing,
        MemoryIdentityStore Store) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed class MemoryIdentityStore(byte[] package) : IDadAutoPartyEndpointIdentityStore, IDisposable
    {
        public ValueTask<string> StoreAsync(ReadOnlyMemory<byte> identityMaterial, CancellationToken cancellationToken = default)
            => ValueTask.FromResult("identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        public ValueTask<byte[]> LoadAsync(string identityReference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(package.ToArray());
        public ValueTask<bool> DeleteAsync(string identityReference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
        public void Dispose() => CryptographicOperations.ZeroMemory(package);
    }
}
