using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using AutoParty.Contracts;
using AutoParty.Core.Authentication;
using dad.Models;

namespace dad.Services;

internal sealed record DadAutoPartyAdapterTransferSnapshot(
    string PayloadType,
    int AcceptedFragmentCount,
    int CurrentFragmentNumber,
    int TotalFragmentCount,
    bool AwaitingCentralAcknowledgement)
{
    internal static DadAutoPartyAdapterTransferSnapshot Idle { get; } = new(string.Empty, 0, 0, 0, false);

    internal bool IsIdle => TotalFragmentCount == 0;
}

public sealed class DadAutoPartyWebhookTransportAdapter : IAutoPartyTransportAdapter, IAsyncDisposable
{
    internal const int MaximumDiscordContentCharacters = AutoPartyProtocol.MaximumCourierTextCharacters;
    private const int MaximumTrackedDeliveries = 256;
    private const int MaximumHttpAttempts = 3;
    private const int MaximumWebhookResponseBytes = 16 * 1024;
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DadAutoPartyWebhookCredential credential;
    private readonly string routeId;
    private readonly long endpointKeyVersion;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeSpan pollInterval;
    private readonly FixedCourierKeyResolver keyResolver;
    private readonly ProductionContractAuthenticator authenticator;
    private readonly Channel<OpaqueEnvelope> outbound = Channel.CreateBounded<OpaqueEnvelope>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    private readonly Channel<OpaqueEnvelope> inbound = Channel.CreateBounded<OpaqueEnvelope>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Channel<AutoPartyTransportAcknowledgement> applicationAcknowledgements =
        Channel.CreateBounded<AutoPartyTransportAcknowledgement>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
    private readonly Queue<PendingCourierAcknowledgement> pendingCourierAcknowledgements = [];
    private readonly Dictionary<Guid, InboundDeliveryState> inboundDeliveries = [];
    private readonly ConcurrentDictionary<Guid, InboundAcknowledgementContext> inboundAcknowledgementContexts = [];
    private readonly HashSet<Guid> completedInboundIds = [];
    private readonly Queue<Guid> completedInboundOrder = [];
    private readonly Dictionary<int, long> observedDownlinkPageGenerations = [];
    private readonly Dictionary<string, string> observedUplinkContents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> observedDownlinkContents = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task pumpTask;
    private CourierEpochDescriptor uplinkEpoch;
    private CourierEpochDescriptor downlinkEpoch;
    private OutboundDeliveryState? activeOutbound;
    private DadAutoPartyEndpointSnapshot snapshot;
    private DadAutoPartyAdapterTransferSnapshot transferSnapshot = DadAutoPartyAdapterTransferSnapshot.Idle;
    private long nextUplinkPageGeneration = Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private DateTime nextPresencePublishUtc = DateTime.MinValue;
    private int pendingOutboundCount;
    private int bufferedInboundCount;
    private bool disposed;

    public DadAutoPartyWebhookTransportAdapter(
        DadAutoPartyWebhookCredential credential,
        string routeId,
        long endpointKeyVersion,
        ReadOnlySpan<byte> endpointSigningPrivateKey,
        HttpClient? httpClient = null,
        bool ownsHttpClient = false,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pollInterval = null)
    {
        if (credential is not { HasProvisionedMailbox: true })
            throw new ArgumentException("A provisioned webhook mailbox is required.", nameof(credential));
        this.routeId = DadAutoPartyConfiguration.NormalizeIdentifier(routeId);
        if (string.IsNullOrWhiteSpace(this.routeId))
            throw new ArgumentException("A bounded route identifier is required.", nameof(routeId));
        if (endpointKeyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(endpointKeyVersion));
        if (endpointSigningPrivateKey.Length != AutoPartyProtocol.Ed25519SignatureBytes / 2)
            throw new ArgumentException("A valid endpoint signing key is required.", nameof(endpointSigningPrivateKey));

        this.credential = credential;
        this.endpointKeyVersion = endpointKeyVersion;
        uplinkEpoch = credential.UplinkEpoch!;
        downlinkEpoch = credential.DownlinkEpoch!;
        keyResolver = new FixedCourierKeyResolver(
            uplinkEpoch.IslandId,
            endpointKeyVersion,
            endpointSigningPrivateKey,
            credential.RelayPublicKeys!);
        authenticator = new ProductionContractAuthenticator(keyResolver);
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        this.ownsHttpClient = httpClient == null || ownsHttpClient;
        this.delay = delay ?? Task.Delay;
        this.pollInterval = pollInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultPollInterval;
        snapshot = new(
            DadAutoPartyEndpointConnectionState.Connecting,
            "dad-webhook-starting",
            DateTime.UtcNow,
            null,
            0,
            0,
            0,
            uplinkEpoch.EpochGeneration);
        pumpTask = Task.Run(() => PumpAsync(shutdown.Token));
    }

    public DadAutoPartyEndpointSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal DadAutoPartyAdapterTransferSnapshot TransferSnapshot => Volatile.Read(ref transferSnapshot);
    public CourierEpochDescriptor UplinkEpochSnapshot => Volatile.Read(ref uplinkEpoch);
    public CourierEpochDescriptor DownlinkEpochSnapshot => Volatile.Read(ref downlinkEpoch);

    public ValueTask<AutoPartyTransportHealth> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Snapshot;
        var state = current.State switch
        {
            DadAutoPartyEndpointConnectionState.Disabled => AutoPartyTransportHealthState.Disabled,
            DadAutoPartyEndpointConnectionState.Ready => AutoPartyTransportHealthState.Ready,
            DadAutoPartyEndpointConnectionState.Degraded => AutoPartyTransportHealthState.Degraded,
            _ => AutoPartyTransportHealthState.NotReady,
        };
        return ValueTask.FromResult(new AutoPartyTransportHealth(
            state,
            current.SafeCode,
            new DateTimeOffset(current.ObservedAtUtc, TimeSpan.Zero)));
    }

    public async IAsyncEnumerable<OpaqueEnvelope> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (inbound.Reader.TryRead(out var delivery))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Decrement(ref bufferedInboundCount);
            UpdateSnapshot(Snapshot.State, Snapshot.SafeCode, Snapshot.LastSuccessfulExchangeAtUtc);
            yield return delivery;
            await Task.Yield();
        }
    }

    public ValueTask<AutoPartyTransportSendResult> SendAsync(
        OpaqueEnvelope delivery,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed)
            return ValueTask.FromResult(Denied(delivery.EnvelopeId, "dad-webhook-disposed"));
        if (!IsBounded(delivery) || delivery.ExpiresAt <= DateTimeOffset.UtcNow)
            return ValueTask.FromResult(Denied(delivery.EnvelopeId, "dad-webhook-envelope-invalid"));
        if (!outbound.Writer.TryWrite(delivery))
            return ValueTask.FromResult(Denied(delivery.EnvelopeId, "dad-webhook-outbound-full"));
        Interlocked.Increment(ref pendingOutboundCount);
        UpdateSnapshot(Snapshot.State, "dad-webhook-outbound-queued", Snapshot.LastSuccessfulExchangeAtUtc);
        return ValueTask.FromResult(new AutoPartyTransportSendResult(
            true,
            "dad-webhook-outbound-queued",
            delivery.EnvelopeId));
    }

    public ValueTask AcknowledgeAsync(
        AutoPartyTransportAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!disposed && acknowledgement.EnvelopeId != Guid.Empty && IsSafeCode(acknowledgement.SafeCode))
            applicationAcknowledgements.Writer.TryWrite(acknowledgement);
        return ValueTask.CompletedTask;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
        try
        {
            UpdateSnapshot(DadAutoPartyEndpointConnectionState.Ready, "dad-webhook-ready", null);
            while (!cancellationToken.IsCancellationRequested)
            {
                DrainApplicationAcknowledgements();
                ExpireInboundDeliveries();
                QueueCompletedInboundDeliveries();
                await PollUplinkAsync(cancellationToken).ConfigureAwait(false);
                await PollDownlinkAsync(cancellationToken).ConfigureAwait(false);
                QueueCompletedInboundDeliveries();
                await PublishUplinkAsync(cancellationToken).ConfigureAwait(false);
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            UpdateSnapshot(DadAutoPartyEndpointConnectionState.Degraded, "dad-webhook-pump-failed", null);
        }
    }

    private async Task PollUplinkAsync(CancellationToken cancellationToken)
    {
        var epoch = UplinkEpochSnapshot;
        foreach (var pageReference in epoch.PageReferences.OrderBy(static page => page.PageNumber))
        {
            var message = await FetchKnownMessageAsync(
                pageReference.MessageReference,
                cancellationToken).ConfigureAwait(false);
            if (message == null)
                continue;
            if (observedUplinkContents.TryGetValue(message.Id, out var priorContent) &&
                string.Equals(priorContent, message.Content, StringComparison.Ordinal))
                continue;

            var accepted = CourierTextCodec.GetKind(message.Content) switch
            {
                CourierTextKind.Empty => true,
                CourierTextKind.Acknowledgement => ProcessUplinkAcknowledgement(
                    message.Content,
                    epoch,
                    pageReference.PageNumber),
                CourierTextKind.Epoch => ProcessEpochUpdate(message.Content, CourierDirection.Uplink),
                _ => false,
            };
            if (accepted)
                observedUplinkContents[message.Id] = message.Content;
        }
    }

    private void DrainApplicationAcknowledgements()
    {
        while (applicationAcknowledgements.Reader.TryRead(out var acknowledgement))
        {
            if (!inboundAcknowledgementContexts.TryRemove(acknowledgement.EnvelopeId, out var context))
                continue;
            pendingCourierAcknowledgements.Enqueue(new(
                context.EpochId,
                CourierDirection.Downlink,
                context.PageNumber,
                context.PageGeneration,
                ImmutableArray<CourierFragmentReceipt>.Empty,
                ImmutableArray.Create(acknowledgement.EnvelopeId),
                acknowledgement.SafeCode));
        }
    }

    private async Task PollDownlinkAsync(CancellationToken cancellationToken)
    {
        var epoch = DownlinkEpochSnapshot;
        foreach (var pageReference in epoch.PageReferences.OrderBy(static page => page.PageNumber))
        {
            var message = await FetchKnownMessageAsync(
                pageReference.MessageReference,
                cancellationToken).ConfigureAwait(false);
            if (message == null)
                continue;
            if (observedDownlinkContents.TryGetValue(message.Id, out var priorContent) &&
                string.Equals(priorContent, message.Content, StringComparison.Ordinal))
                continue;

            var accepted = CourierTextCodec.GetKind(message.Content) switch
            {
                CourierTextKind.Empty => true,
                CourierTextKind.Page => ProcessDownlinkPage(message.Content, epoch, pageReference.PageNumber),
                CourierTextKind.Epoch => ProcessEpochUpdate(message.Content, CourierDirection.Downlink),
                _ => false,
            };
            if (accepted)
                observedDownlinkContents[message.Id] = message.Content;
        }
    }

    private bool ProcessDownlinkPage(
        string content,
        CourierEpochDescriptor expectedEpoch,
        int expectedPageNumber)
    {
        try
        {
            var authenticated = CourierTextCodec.DecodePage(content);
            if (!authenticator.Verify(authenticated).Succeeded)
                return false;
            var page = authenticated.Contract;
            if (!IsRelayHeader(page.Header) ||
                page.EpochId != expectedEpoch.EpochId ||
                page.Direction != CourierDirection.Downlink ||
                page.PageNumber != expectedPageNumber ||
                page.PageCount != expectedEpoch.PageCount ||
                page.PageGeneration < 1 ||
                page.Header.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;

            if (observedDownlinkPageGenerations.TryGetValue(page.PageNumber, out var priorGeneration) &&
                page.PageGeneration < priorGeneration)
                return false;
            observedDownlinkPageGenerations[page.PageNumber] = page.PageGeneration;

            var receipts = ImmutableArray.CreateBuilder<CourierFragmentReceipt>(page.Fragments.Length);
            foreach (var fragment in page.Fragments)
            {
                if (fragment.ExpiresAt <= DateTimeOffset.UtcNow)
                    return false;
                receipts.Add(new CourierFragmentReceipt(fragment.DeliveryId, fragment.FragmentNumber));
                if (completedInboundIds.Contains(fragment.DeliveryId))
                    continue;
                if (!inboundDeliveries.TryGetValue(fragment.DeliveryId, out var state))
                {
                    if (inboundDeliveries.Count >= MaximumTrackedDeliveries)
                        return false;
                    state = new InboundDeliveryState(page.Header, page.EpochId, page.PageNumber, page.PageGeneration);
                    inboundDeliveries.Add(fragment.DeliveryId, state);
                }
                if (!state.TryAdd(fragment, page))
                    return false;
            }

            if (receipts.Count > 0)
            {
                pendingCourierAcknowledgements.Enqueue(new(
                    page.EpochId,
                    CourierDirection.Downlink,
                    page.PageNumber,
                    page.PageGeneration,
                    receipts.MoveToImmutable(),
                    ImmutableArray<Guid>.Empty,
                    "dad-downlink-fragment-accepted"));
            }
            UpdateSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-webhook-downlink-fragment",
                DateTime.UtcNow);
            return true;
        }
        catch (Exception exception) when (
            exception is ProtocolException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private bool ProcessUplinkAcknowledgement(
        string content,
        CourierEpochDescriptor expectedEpoch,
        int expectedPageNumber)
    {
        try
        {
            var authenticated = CourierTextCodec.DecodeAcknowledgement(content);
            if (!authenticator.Verify(authenticated).Succeeded)
                return false;
            var acknowledgement = authenticated.Contract;
            if (!IsRelayHeader(acknowledgement.Header) ||
                acknowledgement.EpochId != expectedEpoch.EpochId ||
                acknowledgement.Direction != CourierDirection.Uplink ||
                acknowledgement.PageNumber != expectedPageNumber ||
                acknowledgement.PageGeneration < 1 ||
                acknowledgement.Header.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;

            if (activeOutbound != null &&
                acknowledgement.PageGeneration == activeOutbound.PageGeneration)
            {
                if (acknowledgement.AcceptedMessageIds.Contains(activeOutbound.Delivery.EnvelopeId))
                {
                    CompleteActiveOutbound();
                }
                else
                {
                    var highestAccepted = acknowledgement.AcceptedFragments
                        .Where(receipt => receipt.DeliveryId == activeOutbound.Delivery.EnvelopeId)
                        .Select(static receipt => receipt.FragmentNumber)
                        .DefaultIfEmpty(0)
                        .Max();
                    if (highestAccepted > activeOutbound.NextFragmentIndex)
                    {
                        activeOutbound.NextFragmentIndex = Math.Min(
                            highestAccepted,
                            activeOutbound.Fragments.Length);
                        activeOutbound.AwaitingAcknowledgement = false;
                        activeOutbound.PublishedContent = string.Empty;
                        if (activeOutbound.NextFragmentIndex >= activeOutbound.Fragments.Length)
                            CompleteActiveOutbound();
                        else
                            UpdateTransferSnapshot(awaitingCentralAcknowledgement: false);
                    }
                }
            }
            UpdateSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-webhook-uplink-fragment-acknowledged",
                DateTime.UtcNow);
            return true;
        }
        catch (Exception exception) when (
            exception is ProtocolException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private bool ProcessEpochUpdate(string content, CourierDirection expectedDirection)
    {
        try
        {
            var authenticated = CourierTextCodec.DecodeEpoch(content);
            if (!authenticator.Verify(authenticated).Succeeded)
                return false;
            var epoch = authenticated.Contract;
            if (!IsRelayHeader(epoch.Header) ||
                epoch.IslandId != DownlinkEpochSnapshot.IslandId ||
                epoch.Direction != expectedDirection ||
                epoch.Header.ExpiresAt <= DateTimeOffset.UtcNow ||
                epoch.PageReferences.Any(page =>
                    DadAutoPartyConfiguration.NormalizeSnowflake(page.MessageReference) != page.MessageReference))
                return false;
            var descriptor = new CourierEpochDescriptor(
                epoch.EpochId,
                epoch.IslandId,
                epoch.Direction,
                epoch.StartsAt,
                epoch.RotatesAt,
                epoch.OverlapEndsAt,
                epoch.PageCount,
                epoch.PageReferences,
                epoch.EpochGeneration);
            if (epoch.Direction == CourierDirection.Uplink)
            {
                var current = UplinkEpochSnapshot;
                if (descriptor.EpochGeneration < current.EpochGeneration ||
                    descriptor.EpochGeneration == current.EpochGeneration && !EpochsMatch(descriptor, current))
                    return false;
                if (descriptor.EpochGeneration > current.EpochGeneration)
                {
                    Volatile.Write(ref uplinkEpoch, descriptor);
                    observedUplinkContents.Clear();
                    if (activeOutbound != null)
                    {
                        activeOutbound.AwaitingAcknowledgement = false;
                        activeOutbound.PublishedContent = string.Empty;
                        UpdateTransferSnapshot(awaitingCentralAcknowledgement: false);
                    }
                }
            }
            else
            {
                var current = DownlinkEpochSnapshot;
                if (descriptor.EpochGeneration < current.EpochGeneration ||
                    descriptor.EpochGeneration == current.EpochGeneration && !EpochsMatch(descriptor, current))
                    return false;
                if (descriptor.EpochGeneration > current.EpochGeneration)
                {
                    Volatile.Write(ref downlinkEpoch, descriptor);
                    observedDownlinkContents.Clear();
                    observedDownlinkPageGenerations.Clear();
                }
            }
            pendingCourierAcknowledgements.Enqueue(new(
                descriptor.EpochId,
                descriptor.Direction,
                1,
                descriptor.EpochGeneration,
                ImmutableArray<CourierFragmentReceipt>.Empty,
                ImmutableArray.Create(epoch.Header.MessageId),
                "dad-courier-epoch-accepted"));
            UpdateSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-webhook-epoch-rotated",
                DateTime.UtcNow);
            return true;
        }
        catch (Exception exception) when (
            exception is ProtocolException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private async Task PublishUplinkAsync(CancellationToken cancellationToken)
    {
        if (pendingCourierAcknowledgements.TryPeek(out var pendingAcknowledgement))
        {
            var content = EncodeAcknowledgement(pendingAcknowledgement);
            var acknowledgementEpoch = GetEpoch(pendingAcknowledgement.Direction);
            var pageReference = acknowledgementEpoch.PageReferences
                .OrderBy(static page => page.PageNumber)
                .First(page => page.PageNumber == pendingAcknowledgement.PageNumber);
            if (await EditKnownMessageAsync(
                    pageReference.MessageReference,
                    content,
                    cancellationToken).ConfigureAwait(false))
            {
                pendingCourierAcknowledgements.Dequeue();
                RememberPublishedContent(
                    pendingAcknowledgement.Direction,
                    pageReference.MessageReference,
                    content);
                UpdateSnapshot(
                    DadAutoPartyEndpointConnectionState.Ready,
                    "dad-webhook-downlink-acknowledged",
                    DateTime.UtcNow);
            }
            else
            {
                UpdateSnapshot(DadAutoPartyEndpointConnectionState.Degraded, "dad-webhook-ack-failed", null);
            }
            return;
        }

        if (activeOutbound == null && outbound.Reader.TryRead(out var delivery))
        {
            if (delivery.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                Interlocked.Decrement(ref pendingOutboundCount);
                return;
            }
            try
            {
                activeOutbound = new OutboundDeliveryState(
                    delivery,
                    CourierFragmentCodec.Fragment(
                        delivery.EnvelopeId,
                        delivery.PayloadType,
                        delivery.Ciphertext.AsSpan(),
                        delivery.ExpiresAt));
                UpdateTransferSnapshot(awaitingCentralAcknowledgement: false);
            }
            catch (ProtocolException)
            {
                Interlocked.Decrement(ref pendingOutboundCount);
                UpdateSnapshot(
                    DadAutoPartyEndpointConnectionState.Degraded,
                    "dad-webhook-envelope-fragmentation-failed",
                    null);
                return;
            }
        }
        if (activeOutbound == null)
        {
            if (DateTime.UtcNow >= nextPresencePublishUtc)
                await PublishPresenceAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (activeOutbound.Delivery.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            CompleteActiveOutbound();
            UpdateSnapshot(
                DadAutoPartyEndpointConnectionState.Degraded,
                "dad-webhook-outbound-expired",
                null);
            return;
        }
        if (activeOutbound.AwaitingAcknowledgement &&
            DateTime.UtcNow - activeOutbound.LastPublishedAtUtc < pollInterval)
            return;

        if (!activeOutbound.AwaitingAcknowledgement)
        {
            var fragment = activeOutbound.Fragments[activeOutbound.NextFragmentIndex];
            var epoch = UplinkEpochSnapshot;
            var pageNumber = ((fragment.FragmentNumber - 1) % epoch.PageCount) + 1;
            activeOutbound.PageNumber = pageNumber;
            activeOutbound.PageGeneration = Interlocked.Increment(ref nextUplinkPageGeneration);
            activeOutbound.PublishedContent = EncodePage(
                epoch,
                pageNumber,
                activeOutbound.PageGeneration,
                fragment);
            activeOutbound.AwaitingAcknowledgement = true;
        }

        var reference = UplinkEpochSnapshot.PageReferences
            .First(page => page.PageNumber == activeOutbound.PageNumber)
            .MessageReference;
        if (await EditKnownMessageAsync(
                reference,
                activeOutbound.PublishedContent,
                cancellationToken).ConfigureAwait(false))
        {
            activeOutbound.LastPublishedAtUtc = DateTime.UtcNow;
            RememberPublishedContent(CourierDirection.Uplink, reference, activeOutbound.PublishedContent);
            UpdateSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-webhook-uplink-fragment-published",
                DateTime.UtcNow);
            UpdateTransferSnapshot(awaitingCentralAcknowledgement: true);
        }
        else
        {
            UpdateSnapshot(DadAutoPartyEndpointConnectionState.Degraded, "dad-webhook-publish-failed", null);
            UpdateTransferSnapshot(awaitingCentralAcknowledgement: false);
        }
    }

    private async Task PublishPresenceAsync(CancellationToken cancellationToken)
    {
        var epoch = UplinkEpochSnapshot;
        var pageReference = epoch.PageReferences
            .OrderBy(static page => page.PageNumber)
            .First();
        var content = EncodePresence(epoch);
        if (await EditKnownMessageAsync(
                pageReference.MessageReference,
                content,
                cancellationToken).ConfigureAwait(false))
        {
            RememberPublishedContent(CourierDirection.Uplink, pageReference.MessageReference, content);
            UpdateSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-webhook-presence-published",
                DateTime.UtcNow);
        }
        else
        {
            UpdateSnapshot(DadAutoPartyEndpointConnectionState.Degraded, "dad-webhook-presence-failed", null);
        }
    }

    private string EncodePage(
        CourierEpochDescriptor epoch,
        int pageNumber,
        long pageGeneration,
        CourierPayloadFragment fragment)
    {
        var header = CreateLocalHeader(
            $"courier-{epoch.EpochId:N}-{pageNumber}-{pageGeneration}",
            pageGeneration,
            epoch.EpochGeneration,
            fragment.ExpiresAt);
        var page = new CourierPage(
            header,
            epoch.EpochId,
            CourierDirection.Uplink,
            pageNumber,
            epoch.PageCount,
            pageGeneration,
            ImmutableArray.Create(fragment));
        return CourierTextCodec.EncodePage(authenticator.Sign(page));
    }

    private string EncodeAcknowledgement(PendingCourierAcknowledgement pending)
    {
        var generation = Interlocked.Increment(ref nextUplinkPageGeneration);
        var header = CreateLocalHeader(
            $"courier-ack-{pending.EpochId:N}-{pending.PageNumber}-{generation}",
            generation,
            GetEpoch(pending.Direction).EpochGeneration,
            DateTimeOffset.UtcNow.AddMinutes(2));
        var acknowledgement = new CourierAcknowledgement(
            header,
            pending.EpochId,
            pending.Direction,
            pending.PageNumber,
            pending.PageGeneration,
            pending.AcceptedFragments,
            pending.AcceptedMessageIds,
            pending.SafeCode);
        return CourierTextCodec.EncodeAcknowledgement(authenticator.Sign(acknowledgement));
    }

    private string EncodePresence(CourierEpochDescriptor epoch)
    {
        var generation = Interlocked.Increment(ref nextUplinkPageGeneration);
        var header = CreateLocalHeader(
            $"courier-presence-{epoch.EpochId:N}-{generation}",
            generation,
            generation,
            DateTimeOffset.UtcNow.AddMinutes(2));
        var presence = new CourierPresence(
            header,
            epoch.EpochId,
            CourierDirection.Uplink,
            epoch.EpochGeneration);
        return CourierTextCodec.EncodePresence(authenticator.Sign(presence));
    }

    private CourierEpochDescriptor GetEpoch(CourierDirection direction) =>
        direction == CourierDirection.Uplink ? UplinkEpochSnapshot : DownlinkEpochSnapshot;

    private static bool EpochsMatch(CourierEpochDescriptor left, CourierEpochDescriptor right) =>
        left.EpochId == right.EpochId &&
        left.IslandId == right.IslandId &&
        left.Direction == right.Direction &&
        left.StartsAt == right.StartsAt &&
        left.RotatesAt == right.RotatesAt &&
        left.OverlapEndsAt == right.OverlapEndsAt &&
        left.PageCount == right.PageCount &&
        left.PageReferences.SequenceEqual(right.PageReferences) &&
        left.EpochGeneration == right.EpochGeneration;

    private void RememberPublishedContent(
        CourierDirection direction,
        string messageReference,
        string content)
    {
        var observed = direction == CourierDirection.Uplink
            ? observedUplinkContents
            : observedDownlinkContents;
        observed[messageReference] = content;
        nextPresencePublishUtc = DateTime.UtcNow + pollInterval;
    }

    private ContractHeader CreateLocalHeader(
        string idempotencyKey,
        long sequence,
        long generation,
        DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= now)
            expiresAt = now.AddMinutes(1);
        var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
        try
        {
            return new ContractHeader(
                AutoPartyProtocol.CurrentVersion,
                Guid.NewGuid(),
                idempotencyKey,
                UplinkEpochSnapshot.IslandId,
                new IslandId(DadAutoPartyIdentityPackageService.RegistrationRecipient),
                now,
                expiresAt,
                Math.Max(1, sequence),
                Math.Max(1, generation),
                endpointKeyVersion,
                credential.RelayPublicKeys!.KeyVersion,
                ContractHeader.CreateNonce(nonce),
                ImmutableArray<int>.Empty);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private bool IsRelayHeader(ContractHeader header) =>
        string.Equals(
            header.SenderIslandId.Value,
            DadAutoPartyIdentityPackageService.RegistrationRecipient,
            StringComparison.Ordinal) &&
        header.RecipientIslandId == DownlinkEpochSnapshot.IslandId &&
        header.SenderKeyVersion == credential.RelayPublicKeys!.KeyVersion &&
        header.RecipientKeyVersion == endpointKeyVersion;

    private void QueueCompletedInboundDeliveries()
    {
        foreach (var pair in inboundDeliveries
                     .Where(static pair => pair.Value.IsComplete)
                     .Take(8)
                     .ToList())
        {
            OpaqueEnvelope delivery;
            try
            {
                var payload = CourierFragmentCodec.Reassemble(pair.Value.Fragments.Values);
                var first = pair.Value.FirstFragment!;
                delivery = OpaqueEnvelope.Create(
                    AutoPartyProtocol.CurrentVersion,
                    first.DeliveryId,
                    pair.Value.Header.SenderIslandId,
                    pair.Value.Header.RecipientIslandId,
                    pair.Value.Header.IssuedAt,
                    first.ExpiresAt,
                    pair.Value.Header.Generation,
                    first.PayloadType,
                    payload.AsSpan());
                if (!IsBounded(delivery))
                    continue;
            }
            catch (ProtocolException)
            {
                inboundDeliveries.Remove(pair.Key);
                continue;
            }
            if (!inbound.Writer.TryWrite(delivery))
            {
                UpdateSnapshot(DadAutoPartyEndpointConnectionState.Degraded, "dad-webhook-inbound-full", null);
                return;
            }
            Interlocked.Increment(ref bufferedInboundCount);
            inboundAcknowledgementContexts[pair.Key] = new(
                pair.Value.EpochId,
                pair.Value.PageNumber,
                pair.Value.PageGeneration);
            inboundDeliveries.Remove(pair.Key);
            RememberCompletedInbound(pair.Key);
            UpdateSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-webhook-downlink-received",
                DateTime.UtcNow);
        }
    }

    private void ExpireInboundDeliveries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in inboundDeliveries
                     .Where(pair => pair.Value.FirstFragment?.ExpiresAt <= now)
                     .Select(static pair => pair.Key)
                     .ToList())
            inboundDeliveries.Remove(id);
    }

    private void RememberCompletedInbound(Guid deliveryId)
    {
        if (!completedInboundIds.Add(deliveryId))
            return;
        completedInboundOrder.Enqueue(deliveryId);
        while (completedInboundOrder.Count > MaximumTrackedDeliveries)
            completedInboundIds.Remove(completedInboundOrder.Dequeue());
    }

    private void CompleteActiveOutbound()
    {
        if (activeOutbound == null)
            return;
        activeOutbound = null;
        Volatile.Write(ref transferSnapshot, DadAutoPartyAdapterTransferSnapshot.Idle);
        Interlocked.Decrement(ref pendingOutboundCount);
    }

    private void UpdateTransferSnapshot(bool awaitingCentralAcknowledgement)
    {
        var outboundState = activeOutbound;
        if (outboundState == null)
        {
            Volatile.Write(ref transferSnapshot, DadAutoPartyAdapterTransferSnapshot.Idle);
            return;
        }
        Volatile.Write(ref transferSnapshot, new DadAutoPartyAdapterTransferSnapshot(
            outboundState.Delivery.PayloadType,
            Math.Clamp(outboundState.NextFragmentIndex, 0, outboundState.Fragments.Length),
            Math.Min(outboundState.NextFragmentIndex + 1, outboundState.Fragments.Length),
            outboundState.Fragments.Length,
            awaitingCentralAcknowledgement));
    }

    private async Task<WebhookMessage?> FetchKnownMessageAsync(
        string messageReference,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                BuildWebhookPath($"/messages/{messageReference}")),
            cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            UpdateSnapshot(DadAutoPartyEndpointConnectionState.Degraded, "dad-webhook-fetch-failed", null);
            return null;
        }
        var message = await ReadWebhookMessageAsync(response, cancellationToken).ConfigureAwait(false);
        return message != null && string.Equals(message.Id, messageReference, StringComparison.Ordinal)
            ? message
            : null;
    }

    private async Task<bool> EditKnownMessageAsync(
        string messageReference,
        string content,
        CancellationToken cancellationToken)
    {
        if (content.Length is < 1 or > MaximumDiscordContentCharacters)
            return false;
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Patch,
                BuildWebhookPath($"/messages/{messageReference}"))
            {
                Content = JsonContent.Create(new WebhookWriteRequest(
                    content,
                    new AllowedMentions([]))),
            },
            cancellationToken).ConfigureAwait(false);
        if (response == null)
            return false;
        var message = await ReadWebhookMessageAsync(response, cancellationToken).ConfigureAwait(false);
        return message != null && string.Equals(message.Id, messageReference, StringComparison.Ordinal);
    }

    private static async Task<WebhookMessage?> ReadWebhookMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumWebhookResponseBytes)
            return null;
        var buffer = new byte[MaximumWebhookResponseBytes + 1];
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                length += read;
            }
            if (length is <= 0 or > MaximumWebhookResponseBytes)
                return null;
            var message = JsonSerializer.Deserialize<WebhookMessage>(buffer.AsSpan(0, length), JsonOptions);
            return message is { Content.Length: > 0 and <= MaximumDiscordContentCharacters }
                ? message
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task<HttpResponseMessage?> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumHttpAttempts; attempt++)
        {
            using var request = requestFactory();
            HttpResponseMessage? response = null;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return response;
                if (response.StatusCode is not HttpStatusCode.TooManyRequests &&
                    (int)response.StatusCode < 500)
                {
                    response.Dispose();
                    return null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (HttpRequestException)
            {
            }
            response?.Dispose();
            if (attempt < MaximumHttpAttempts)
                await delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private string BuildWebhookPath(string suffix) =>
        $"https://discord.com/api/v10/webhooks/{credential.WebhookId}/{credential.WebhookToken}{suffix}";

    private void UpdateSnapshot(
        DadAutoPartyEndpointConnectionState state,
        string safeCode,
        DateTime? lastSuccessfulExchangeAtUtc)
    {
        var prior = Snapshot;
        Volatile.Write(ref snapshot, new DadAutoPartyEndpointSnapshot(
            state,
            DadAutoPartyConfiguration.NormalizeSafeCode(safeCode),
            DateTime.UtcNow,
            lastSuccessfulExchangeAtUtc ?? prior.LastSuccessfulExchangeAtUtc,
            Math.Max(0, Volatile.Read(ref pendingOutboundCount)),
            applicationAcknowledgements.Reader.Count + pendingCourierAcknowledgements.Count,
            Math.Max(0, Volatile.Read(ref bufferedInboundCount)),
            Math.Max(UplinkEpochSnapshot.EpochGeneration, DownlinkEpochSnapshot.EpochGeneration)));
    }

    private static bool IsBounded(OpaqueEnvelope delivery) =>
        delivery.EnvelopeVersion == AutoPartyProtocol.CurrentVersion &&
        delivery.EnvelopeId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(delivery.SenderIslandId.Value) &&
        delivery.SenderIslandId.Value.Length <= AutoPartyProtocol.MaximumIdentifierLength &&
        !string.IsNullOrWhiteSpace(delivery.RecipientIslandId.Value) &&
        delivery.RecipientIslandId.Value.Length <= AutoPartyProtocol.MaximumIdentifierLength &&
        delivery.IssuedAt.Offset == TimeSpan.Zero &&
        delivery.ExpiresAt.Offset == TimeSpan.Zero &&
        delivery.IssuedAt < delivery.ExpiresAt &&
        delivery.Generation > 0 &&
        delivery.PayloadLength is > 0 and <= AutoPartyProtocol.MaximumOpaquePayloadBytes &&
        !string.IsNullOrWhiteSpace(delivery.PayloadType) &&
        delivery.PayloadType.Length <= AutoPartyProtocol.MaximumIdentifierLength;

    private static bool IsSafeCode(string? value) =>
        DadAutoPartyConfiguration.NormalizeSafeCode(value) == value;

    private static AutoPartyTransportSendResult Denied(Guid id, string code) => new(false, code, id);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        outbound.Writer.TryComplete();
        applicationAcknowledgements.Writer.TryComplete();
        shutdown.Cancel();
        if (ownsHttpClient)
            httpClient.Dispose();
        try
        {
            await pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        shutdown.Dispose();
        keyResolver.Dispose();
        inboundDeliveries.Clear();
        inboundAcknowledgementContexts.Clear();
        completedInboundIds.Clear();
        Volatile.Write(ref transferSnapshot, DadAutoPartyAdapterTransferSnapshot.Idle);
        UpdateSnapshot(DadAutoPartyEndpointConnectionState.Disabled, "dad-webhook-disposed", null);
    }

    private sealed record AllowedMentions(IReadOnlyList<string> Parse);
    private sealed record WebhookWriteRequest(string Content, AllowedMentions AllowedMentions);
    private sealed record WebhookMessage(string Id, string Content);
    private sealed record PendingCourierAcknowledgement(
        Guid EpochId,
        CourierDirection Direction,
        int PageNumber,
        long PageGeneration,
        ImmutableArray<CourierFragmentReceipt> AcceptedFragments,
        ImmutableArray<Guid> AcceptedMessageIds,
        string SafeCode);
    private sealed record InboundAcknowledgementContext(
        Guid EpochId,
        int PageNumber,
        long PageGeneration);

    private sealed class OutboundDeliveryState(
        OpaqueEnvelope delivery,
        ImmutableArray<CourierPayloadFragment> fragments)
    {
        public OpaqueEnvelope Delivery { get; } = delivery;
        public ImmutableArray<CourierPayloadFragment> Fragments { get; } = fragments;
        public int NextFragmentIndex { get; set; }
        public int PageNumber { get; set; }
        public long PageGeneration { get; set; }
        public bool AwaitingAcknowledgement { get; set; }
        public string PublishedContent { get; set; } = string.Empty;
        public DateTime LastPublishedAtUtc { get; set; } = DateTime.MinValue;
    }

    private sealed class InboundDeliveryState(
        ContractHeader header,
        Guid epochId,
        int pageNumber,
        long pageGeneration)
    {
        public ContractHeader Header { get; } = header;
        public Guid EpochId { get; } = epochId;
        public int PageNumber { get; private set; } = pageNumber;
        public long PageGeneration { get; private set; } = pageGeneration;
        public Dictionary<int, CourierPayloadFragment> Fragments { get; } = [];
        public CourierPayloadFragment? FirstFragment =>
            Fragments.TryGetValue(1, out var first) ? first : null;
        public bool IsComplete =>
            FirstFragment is { } first && Fragments.Count == first.FragmentCount;

        public bool TryAdd(CourierPayloadFragment fragment, CourierPage page)
        {
            if (FirstFragment is { } first &&
                (first.DeliveryId != fragment.DeliveryId ||
                 first.FragmentCount != fragment.FragmentCount ||
                 !string.Equals(first.PayloadType, fragment.PayloadType, StringComparison.Ordinal) ||
                 first.ExpiresAt != fragment.ExpiresAt ||
                 !first.PayloadSha256.AsSpan().SequenceEqual(fragment.PayloadSha256.AsSpan())))
                return false;
            if (Fragments.TryGetValue(fragment.FragmentNumber, out var existing))
                return existing.Payload.AsSpan().SequenceEqual(fragment.Payload.AsSpan());
            Fragments.Add(fragment.FragmentNumber, fragment);
            PageNumber = page.PageNumber;
            PageGeneration = page.PageGeneration;
            return true;
        }
    }

    private sealed class FixedCourierKeyResolver : IContractKeyResolver, IDisposable
    {
        private readonly IslandId localIslandId;
        private readonly long localKeyVersion;
        private readonly byte[] localSigningPrivateKey;
        private readonly long relayKeyVersion;
        private readonly byte[] relaySigningPublicKey;
        private bool disposed;

        public FixedCourierKeyResolver(
            IslandId localIslandId,
            long localKeyVersion,
            ReadOnlySpan<byte> localSigningPrivateKey,
            EndpointPublicKeys relayPublicKeys)
        {
            this.localIslandId = localIslandId;
            this.localKeyVersion = localKeyVersion;
            this.localSigningPrivateKey = localSigningPrivateKey.ToArray();
            relayKeyVersion = relayPublicKeys.KeyVersion;
            relaySigningPublicKey = relayPublicKeys.Ed25519PublicKey.ToArray();
        }

        public bool TryGetEd25519PrivateKey(
            IslandId islandId,
            long keyVersion,
            out ReadOnlyMemory<byte> privateKey)
        {
            if (!disposed && islandId == localIslandId && keyVersion == localKeyVersion)
            {
                privateKey = localSigningPrivateKey;
                return true;
            }
            privateKey = default;
            return false;
        }

        public bool TryGetEd25519PublicKey(
            IslandId islandId,
            long keyVersion,
            out ReadOnlyMemory<byte> publicKey)
        {
            if (!disposed &&
                string.Equals(
                    islandId.Value,
                    DadAutoPartyIdentityPackageService.RegistrationRecipient,
                    StringComparison.Ordinal) &&
                keyVersion == relayKeyVersion)
            {
                publicKey = relaySigningPublicKey;
                return true;
            }
            publicKey = default;
            return false;
        }

        public bool TryGetX25519PrivateKey(
            IslandId islandId,
            long keyVersion,
            out ReadOnlyMemory<byte> privateKey)
        {
            privateKey = default;
            return false;
        }

        public bool TryGetX25519PublicKey(
            IslandId islandId,
            long keyVersion,
            out ReadOnlyMemory<byte> publicKey)
        {
            publicKey = default;
            return false;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            CryptographicOperations.ZeroMemory(localSigningPrivateKey);
            CryptographicOperations.ZeroMemory(relaySigningPublicKey);
        }
    }
}
