using System.Xml.Linq;
using Xunit;

namespace dad.Tests;

public sealed class DadLocalizationCatalogTests
{
    [Fact]
    public void UiInitializationPrecedesNativeOwnersAndTransportStartup()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot().FullName, "Plugin.cs"));
        var constructor = source[source.IndexOf("public Plugin()", StringComparison.Ordinal)..];
        var appearance = constructor.IndexOf("ApplyAppearance();", StringComparison.Ordinal);
        Assert.True(appearance >= 0);
        foreach (var owner in new[]
                 {
                     "DependencyService = new", "TitleMenuReadinessService = new",
                     "TransportService = new", "CommandManager.AddHandler",
                     "Framework.Update += OnFrameworkUpdate",
                 })
            Assert.True(appearance < constructor.IndexOf(owner, StringComparison.Ordinal), owner);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh_Hans")]
    [InlineData("vi")]
    [InlineData("pt_BR")]
    [InlineData("id")]
    [InlineData("pl")]
    [InlineData("tr")]
    [InlineData("hi")]
    public void EveryUiLanguageCanBuildTheExactOrdinalEnglishLookup(string language)
    {
        var english = ReadCatalog("en");
        // UiText's constructor indexes English display text, not resource IDs.
        // A second "Auto" label under a different ID must fail here, not in game.
        var labels = english.ToDictionary(row => row.Value, row => row.Key, StringComparer.Ordinal);
        var localized = ReadCatalog(language).ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        Assert.Equal(labels.Count, localized.Count);
        Assert.All(labels.Values, key => Assert.True(
            localized.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value),
            $"Missing {language} translation: {key}"));
        Assert.Equal("text_0222", labels["Auto"]);
        Assert.False(string.IsNullOrEmpty(localized[labels["Auto"]]));
        Assert.Contains("Picker", labels.Keys);
        Assert.Contains("Recover game connections automatically", labels.Keys);
        Assert.Contains("Stay on visited world/DC between presets", labels.Keys);
        // UiGui translates these reached repair selectors and help text by their English display text.
        foreach (var caption in new[]
                 {
                     "NPC, allowing inn fallback", "NPC repair, then enter inn", "Repair inn destination",
                     "Automatic (ADS)", "Ul'dah", "Gridania", "Limsa Lominsa", "Ishgard",
                     "The Crystarium", "Old Sharlayan", "Tuliyollal",
                     "ADS owns the repair and return-to-inn route. DAD also waits for the ADS utility to finish before queueing, within the existing 180-second preparation timeout. All workers need an ADS version supporting this route.",
                 })
            Assert.Contains(caption, labels.Keys);
    }

    private static List<KeyValuePair<string, string>> ReadCatalog(string language)
        => XDocument.Load(Path.Combine(RepositoryRoot().FullName, "Localization", $"Strings_{language}.resx"))
            .Root!.Elements("data")
            .Select(row => new KeyValuePair<string, string>(row.Attribute("name")!.Value, row.Element("value")!.Value))
            .ToList();

    private static DirectoryInfo RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "dad.csproj")))
            root = root.Parent;
        Assert.NotNull(root);
        return root;
    }
}
