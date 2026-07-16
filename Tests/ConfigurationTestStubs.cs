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
