using System.Net.Sockets;
using dad.Models;
using dad.Services;
using Xunit;
using Xunit.Abstractions;

namespace dad.Tests;

public sealed class DadLiveStatusProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task QueryConfiguredAuthorityStatusReadOnly()
    {
        var endpoint = Environment.GetEnvironmentVariable("DAD_LIVE_ENDPOINT");
        var secret = Environment.GetEnvironmentVariable("DAD_LIVE_SECRET");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(secret))
        {
            output.WriteLine("Read-only live probe not configured; set DAD_LIVE_ENDPOINT and DAD_LIVE_SECRET to opt in.");
            return;
        }
        var separator = endpoint.LastIndexOf(':');
        var host = endpoint[..separator];
        var port = int.Parse(endpoint[(separator + 1)..]);
        var worker = new DadWorkerSessionId($"dad-readonly-probe-{Guid.NewGuid():N}");
        var clientId = $"dad-readonly-probe-{Guid.NewGuid():N}";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        var helloCorrelation = Guid.NewGuid().ToString("N");
        await DadHubProtocol.WriteFrameAsync(
            stream,
            DadHubProtocol.CreateFrame(
                DadHubFrameKind.Hello,
                worker,
                new DadWorkerSessionId(string.Empty),
                "hello",
                helloCorrelation,
                DadIpcJson.Serialize(new DadHubHello
                {
                    ClientInstanceId = clientId,
                    WorkerSessionId = worker,
                    BuildVersion = "readonly-status-probe",
                    Participant = new DadParticipantSnapshot
                    {
                        ClientInstanceId = clientId,
                        WorkerSessionId = worker,
                        Role = DadOrchestrationRole.Participant,
                        WorkerRole = DadWorkerRole.ClientDad,
                        IsAvailable = false,
                        IsEligibleForRun = false,
                        StatusText = "Read-only live status probe",
                    },
                }),
                secret),
            timeout.Token);

        var helloAck = await DadHubProtocol.ReadFrameAsync(stream, timeout.Token)
                       ?? throw new InvalidOperationException("Authority closed before hello acknowledgement.");
        DadHubProtocol.ValidateFrame(helloAck, secret);
        Assert.Equal(DadHubFrameKind.HelloAck, helloAck.Kind);
        Assert.Equal(helloCorrelation, helloAck.CorrelationId);
        var server = helloAck.SourceWorkerSessionId;

        var statusCorrelation = Guid.NewGuid().ToString("N");
        await DadHubProtocol.WriteFrameAsync(
            stream,
            DadHubProtocol.CreateFrame(
                DadHubFrameKind.Request,
                worker,
                server,
                "status-query",
                statusCorrelation,
                DadIpcJson.Serialize(string.Empty),
                secret),
            timeout.Token);

        DadHubFrame? response;
        do
        {
            response = await DadHubProtocol.ReadFrameAsync(stream, timeout.Token);
        }
        while (response != null && !string.Equals(response.CorrelationId, statusCorrelation, StringComparison.Ordinal));

        Assert.NotNull(response);
        DadHubProtocol.ValidateFrame(response!, secret);
        Assert.Equal(DadHubFrameKind.Response, response!.Kind);
        var result = DadIpcJson.Deserialize<DadRunResult>(response.PayloadJson);
        Assert.NotNull(result);
        output.WriteLine(
            "STATUS request={0} status={1} phase={2} task={3} taskStatus={4} executorPulse={5} executorPhase={6} blocked={7} failure={8} summary={9}",
            result!.RequestId,
            result.Status,
            result.Phase,
            result.ActiveTaskName,
            result.ActiveTaskStatus,
            result.CurrentExecutorStatus.StepName,
            result.CurrentExecutorStatus.Phase,
            result.BlockedReason,
            result.FailureReason,
            result.Summary);
    }
}
