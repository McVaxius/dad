using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadGatherListImportTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ReadsNativeVersionedGzipExportsAndRetainsPreferredNodesAndDisabledItems(int version)
    {
        var export = Export(version, new
        {
            Name = "Timed materials",
            ItemIds = new uint[] { 42, 43 },
            Quantities = new Dictionary<string, int> { ["42"] = 1000, ["43"] = 50 },
            PrefferedLocations = new Dictionary<string, uint> { ["42"] = 77 },
            EnabledItems = new Dictionary<string, bool> { ["42"] = true, ["43"] = false },
        });
        var result = DadGatherListImport.Parse(export, _ => throw new Exception("GBR IDs need no name lookup"));
        Assert.True(result.Succeeded, string.Join("; ", result.Issues));
        Assert.Equal("Timed materials", result.Task.Name);
        Assert.Equal(new uint[] { 42, 43 }, result.Task.Items.Select(item => item.ItemId));
        Assert.Equal(77u, result.Task.Items[0].PreferredLocationId);
        Assert.Equal(1000, result.Task.Items[0].TargetQuantity);
        Assert.False(result.Task.Items[1].Enabled);
    }

    [Fact]
    public void ReadsTeamcraftQuantityFirstExportsAndIdRowsWithoutCurrentCultureChangingQuantities()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = DadGatherListImport.Parse("Gathering:\n1000x Iron Ore\n20x Dark Matter Cluster\n42,5\n43 7", Identify);
            Assert.True(result.Succeeded, string.Join("; ", result.Issues));
            Assert.Equal(1005, result.Task.Items.Single(item => item.ItemId == 42).TargetQuantity);
            Assert.Equal(27, result.Task.Items.Single(item => item.ItemId == 43).TargetQuantity);
            Assert.Equal(2, result.Task.Items.Count);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an export")]
    [InlineData("0x Iron Ore")]
    [InlineData("1000000x Iron Ore")]
    [InlineData("999999x Iron Ore\n1x Iron Ore")]
    [InlineData("H4sIinvalid")]
    [InlineData("1x Unknown Material")]
    public void InvalidRowsAndExportsHaveVisibleIssuesInsteadOfSilentSuccess(string input)
    {
        var result = DadGatherListImport.Parse(input, Identify);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void UnknownNamedRowRemainsAvailableForCorrection()
    {
        var result = DadGatherListImport.Parse("3x Unknown Material\n10x Iron Ore", Identify);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Task.Items.Count);
        Assert.Equal("Unknown Material", result.Task.Items[0].Name);
        Assert.Equal(0u, result.Task.Items[0].ItemId);
        Assert.Equal(3, result.Task.Items[0].TargetQuantity);
    }

    [Fact]
    public void IncompleteOrUnsupportedNativeExportsReturnIssues()
    {
        Assert.False(DadGatherListImport.Parse(Export(6, new { ItemIds = new[] { 42 } }), Identify).Succeeded);
        Assert.False(DadGatherListImport.Parse(Export(7, new { }), Identify).Succeeded);
    }

    [Fact]
    public void ExpandedGzipPayloadAndHugeInputAreBounded()
    {
        Assert.False(DadGatherListImport.Parse(new string('x', 1024 * 1024 + 1), Identify).Succeeded);
        Assert.False(DadGatherListImport.Parse(Export(6, new { Description = new string('x', 1024 * 1024 + 1) }), Identify).Succeeded);
    }

    [Fact]
    public void DuplicateNativeItemIdsAreNotMultipliedIntoAnAccidentalQuota()
    {
        var result = DadGatherListImport.Parse(Export(6, new
        {
            ItemIds = new[] { 42, 42 }, Quantities = new Dictionary<string, int> { ["42"] = 100 },
            PrefferedLocations = new Dictionary<string, int>(),
        }), Identify);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("more than once"));
    }

    private static uint Identify(string name) => name switch
    {
        "Iron Ore" => 42,
        "Dark Matter Cluster" => 43,
        _ => 0,
    };

    private static string Export(int version, object config)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
        {
            gzip.WriteByte((byte)version);
            gzip.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config)));
        }
        return Convert.ToBase64String(output.ToArray());
    }
}
