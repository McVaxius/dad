namespace Dalamud.Configuration
{
    public interface IPluginConfiguration
    {
        int Version { get; set; }
    }
}

namespace dad
{
    internal static class Plugin
    {
        public static TestPluginInterface PluginInterface { get; } = new();
    }

    internal sealed class TestPluginInterface
    {
        public int SaveCount { get; private set; }

        public void SavePluginConfig(object configuration)
            => SaveCount++;

        public void Reset()
            => SaveCount = 0;
    }
}

namespace dad.Tests
{
    internal sealed class MissingAutoPartyIdentityStore : Services.IDadAutoPartyEndpointIdentityStore
    {
        public ValueTask<string> StoreAsync(
            ReadOnlyMemory<byte> identityMaterial,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<byte[]> LoadAsync(
            string identityReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            string identityReference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    internal sealed class MissingAutoPartyWebhookStore : Services.IDadAutoPartyWebhookCredentialStore
    {
        public ValueTask<string> StoreAsync(
            Models.DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Models.DadAutoPartyWebhookCredential> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ReplaceAsync(
            string credentialReference,
            Models.DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }
}
