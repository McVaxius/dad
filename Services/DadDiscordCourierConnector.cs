using System.Runtime.CompilerServices;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

public sealed class DadDiscordCourierConnector : IAutoPartyTransportAdapter, IAsyncDisposable
{
    private readonly Func<bool> dadEnabled;
    private IAutoPartyTransportAdapter? innerAdapter;
    private bool disposed;

    public DadDiscordCourierConnector(
        DadAutoPartyConfiguration configuration,
        Func<bool> dadEnabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.dadEnabled = dadEnabled ?? throw new ArgumentNullException(nameof(dadEnabled));
    }

    public void AttachVerifiedAdapter(IAutoPartyTransportAdapter adapter)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        innerAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public void DetachAdapter()
        => innerAdapter = null;

    public async ValueTask<AutoPartyTransportHealth> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || !dadEnabled())
            return Health(AutoPartyTransportHealthState.Disabled, "dad-autoparty-disabled");
        if (innerAdapter == null)
            return Health(AutoPartyTransportHealthState.NotReady, "dad-courier-not-attached");

        try
        {
            return await innerAdapter.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Health(AutoPartyTransportHealthState.Degraded, "dad-courier-health-failed");
        }
    }

    public async IAsyncEnumerable<OpaqueEnvelope> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (disposed || !dadEnabled() || innerAdapter == null)
            yield break;

        await foreach (var delivery in innerAdapter.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!IsBounded(delivery) || delivery.ExpiresAt <= DateTimeOffset.UtcNow)
                continue;
            yield return delivery;
        }
    }

    public async ValueTask<AutoPartyTransportSendResult> SendAsync(
        OpaqueEnvelope delivery,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || !dadEnabled())
            return Denied(delivery.EnvelopeId, "dad-autoparty-disabled");
        if (innerAdapter == null)
            return Denied(delivery.EnvelopeId, "dad-courier-not-attached");
        if (!IsBounded(delivery))
            return Denied(delivery.EnvelopeId, "dad-courier-envelope-invalid");

        try
        {
            return await innerAdapter.SendAsync(delivery, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Denied(delivery.EnvelopeId, "dad-courier-send-failed");
        }
    }

    public async ValueTask AcknowledgeAsync(
        AutoPartyTransportAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || !dadEnabled() || innerAdapter == null)
            return;
        await innerAdapter.AcknowledgeAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        if (innerAdapter is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (innerAdapter is IDisposable disposable)
            disposable.Dispose();
        innerAdapter = null;
    }

    private static bool IsBounded(OpaqueEnvelope delivery)
        => delivery.EnvelopeVersion == AutoPartyProtocol.CurrentVersion &&
           delivery.EnvelopeId != Guid.Empty &&
           !string.IsNullOrWhiteSpace(delivery.SenderIslandId.Value) &&
           !string.IsNullOrWhiteSpace(delivery.RecipientIslandId.Value) &&
           delivery.IssuedAt < delivery.ExpiresAt &&
           delivery.PayloadLength is > 0 and <= AutoPartyProtocol.MaximumSemanticEnvelopeBytes &&
           !string.IsNullOrWhiteSpace(delivery.PayloadType) &&
           delivery.PayloadType.Length <= AutoPartyProtocol.MaximumIdentifierLength;

    private static AutoPartyTransportHealth Health(
        AutoPartyTransportHealthState state,
        string safeCode)
        => new(state, safeCode, DateTimeOffset.UtcNow);

    private static AutoPartyTransportSendResult Denied(Guid envelopeId, string safeCode)
        => new(false, safeCode, envelopeId);
}
