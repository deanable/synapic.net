using System.Text.Json;
using Synapic.Avalonia.Services.Daminion;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The GetDefaultLayout response shape differs across Daminion builds: older
/// servers return a flat array keyed name/guid; server 11.0.0.3906 wraps the
/// same entries in nested "properties" arrays keyed propertyName/propertyGuid.
/// The old parser read only the flat shape, so the GUID map came back empty on
/// the live server and every metadata write degraded to "No metadata
/// operations". These tests pin both shapes (payloads captured live).
/// </summary>
public class DaminionLayoutParsingTests
{
    private static JsonElement Parse(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Nested_server_11_shape_yields_all_tag_pairs()
    {
        const string json = """
            {
              "type": 0,
              "guid": "7b3f-internal",
              "properties": [
                {
                  "type": 5,
                  "guid": "layout-group",
                  "properties": [
                    { "propertyName": "Name", "propertyGuid": "11111111-1111-1111-1111-111111111111", "canSort": true },
                    { "propertyName": "Flag", "propertyGuid": "22222222-2222-2222-2222-222222222222", "canSort": true },
                    { "propertyName": "Creation Date", "propertyGuid": "33333333-3333-3333-3333-333333333333", "canSort": true },
                    { "propertyName": "Rating", "propertyGuid": "44444444-4444-4444-4444-444444444444", "canSort": true },
                    { "propertyName": "Keywords", "propertyGuid": "55555555-5555-5555-5555-555555555555", "canSort": true }
                  ]
                },
                {
                  "type": 5,
                  "guid": "layout-group-2",
                  "properties": [
                    { "propertyName": "Sublocation", "propertyGuid": "66666666-6666-6666-6666-666666666666", "canSort": true }
                  ]
                }
              ]
            }
            """;

        var pairs = DaminionApiClient.ExtractLayoutTagPairs(Parse(json)).ToList();

        Assert.Equal(6, pairs.Count);
        Assert.Contains(pairs, p => p.Name == "Name" && p.Guid == "11111111-1111-1111-1111-111111111111");
        Assert.Contains(pairs, p => p.Name == "Flag" && p.Guid == "22222222-2222-2222-2222-222222222222");
        Assert.Contains(pairs, p => p.Name == "Sublocation" && p.Guid == "66666666-6666-6666-6666-666666666666");
    }

    [Fact]
    public void Flat_legacy_shape_yields_tag_pairs()
    {
        const string json = """
            [
              { "name": "Name", "guid": "11111111-1111-1111-1111-111111111111" },
              { "name": "Keywords", "guid": "55555555-5555-5555-5555-555555555555" }
            ]
            """;

        var pairs = DaminionApiClient.ExtractLayoutTagPairs(Parse(json)).ToList();

        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, p => p.Name == "Name" && p.Guid == "11111111-1111-1111-1111-111111111111");
        Assert.Contains(pairs, p => p.Name == "Keywords" && p.Guid == "55555555-5555-5555-5555-555555555555");
    }

    [Fact]
    public void Entries_without_name_or_guid_are_skipped()
    {
        const string json = """
            {
              "properties": [
                { "propertyName": "Flag", "propertyGuid": "22222222-2222-2222-2222-222222222222" },
                { "propertyName": "NoGuidHere" },
                { "propertyGuid": "33333333-3333-3333-3333-333333333333" },
                { "propertyName": 12, "propertyGuid": "44444444-4444-4444-4444-444444444444" }
              ]
            }
            """;

        var pairs = DaminionApiClient.ExtractLayoutTagPairs(Parse(json)).ToList();

        Assert.Single(pairs);
        Assert.Equal("Flag", pairs[0].Name);
    }

    [Fact]
    public void Deeply_nested_levels_are_reached()
    {
        const string json = """
            {
              "properties": [
                { "properties": [ { "properties": [
                  { "propertyName": "Keywords", "propertyGuid": "55555555-5555-5555-5555-555555555555" }
                ] } ] }
              ]
            }
            """;

        var pairs = DaminionApiClient.ExtractLayoutTagPairs(Parse(json)).ToList();

        Assert.Single(pairs);
        Assert.Equal(("Keywords", "55555555-5555-5555-5555-555555555555"), pairs[0]);
    }
}
