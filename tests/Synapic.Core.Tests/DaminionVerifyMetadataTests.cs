using System.Text.Json;
using Synapic.Core.Services.Daminion;
using Xunit;

namespace Synapic.Core.Tests;

/// <summary>
/// "Verify Daminion writes" reads the catalog back and names what did not
/// land. The payload it reads is the server's own shape — tag name in
/// <c>propertyName</c>, value in <c>propertyValue</c> or the multi-value
/// <c>values</c> array — so the matcher has to be keyed on tag names. Keyed on
/// the JSON's member names it found only the words "propertyName" and
/// "propertyValue", and reported every written field missing on a write that
/// had actually landed.
/// </summary>
public class DaminionVerifyMetadataTests
{
    // Recorded from /api/ItemData/GetAll on server 11.x (the shape the capture
    // date reader is pinned to), with the three fields phase 10 writes.
    private const string LiveShapeJson = """
    {
      "properties": [
        { "name": "Metadata", "id": "0", "properties": [
            { "propertyName": "Categories", "propertyGuid": "g1", "propertyValue": "Synapic Live Acceptance", "values": null, "isMultiplyValues": false, "propertyType": "String" },
            { "propertyName": "Keywords", "propertyGuid": "g2", "propertyValue": "", "values": [
                { "text": "synapic-live-2026-10-23", "id": 811, "tagName": "Keywords", "rawValue": "synapic-live-2026-10-23" },
                { "text": "forest", "id": 812, "tagName": "Keywords", "rawValue": "forest" } ], "isMultiplyValues": true, "propertyType": "String" },
            { "propertyName": "Description", "propertyGuid": "g3", "propertyValue": "Synapic live acceptance 2026-10-23", "values": null, "isMultiplyValues": false, "propertyType": "String" },
            { "propertyName": "Creation Datetime", "propertyGuid": "g4", "propertyValue": "2024.05.01.09.30.00", "values": null, "isMultiplyValues": false, "propertyType": "DateTime" }
        ]}
      ],
      "id": 42,
      "hashCode": 12345
    }
    """;

    private static IReadOnlyList<(string Key, string Value)> Pairs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return DaminionApiClient.TagPairsFromItemData(doc.RootElement);
    }

    [Fact]
    public void A_write_that_landed_verifies_on_the_server_payload()
    {
        var missing = DaminionApiClient.FindMissingFields(
            Pairs(LiveShapeJson),
            category: "Synapic Live Acceptance",
            keywords: new[] { "synapic-live-2026-10-23" },
            description: "Synapic live acceptance 2026-10-23");

        Assert.Empty(missing);
    }

    [Fact]
    public void A_missing_keyword_is_named_and_not_matched_as_a_substring()
    {
        var missing = DaminionApiClient.FindMissingFields(
            Pairs(LiveShapeJson),
            category: null,
            keywords: new[] { "synapic-live-2026-10-2", "forest" },
            description: null);

        // "synapic-live-2026-10-2" is a prefix of the stored value, not the value.
        Assert.Single(missing);
        Assert.Contains("'synapic-live-2026-10-2'", missing[0]);
    }

    [Fact]
    public void A_missing_category_and_description_are_both_named()
    {
        var missing = DaminionApiClient.FindMissingFields(
            Pairs(LiveShapeJson),
            category: "Portraits",
            keywords: null,
            description: "a different description entirely");

        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, m => m.Contains("category 'Portraits'"));
        Assert.Contains(missing, m => m == "description");
    }

    [Fact]
    public void The_value_objects_of_a_multi_value_tag_are_read()
    {
        // Observed on the live catalog: Keywords/Categories carry value *objects*
        // ({ text, id, tagName, rawValue }), while a single-value field carries
        // its value as a string. A reader that only accepts strings sees a tagged
        // item as untagged — the write then looks like it never landed.
        var pairs = Pairs(LiveShapeJson);

        Assert.Contains(pairs, p => p.Key == "Categories" && p.Value == "Synapic Live Acceptance");
        Assert.Contains(pairs, p => p.Key == "Keywords" && p.Value == "forest");
    }

    [Theory]
    [InlineData("Categories", "Synapic Live Acceptance", true)]
    [InlineData("categories", "synapic live acceptance", true)]
    [InlineData("keywords", "  forest  ", true)]
    [InlineData("Keywords", "fore", false)]
    [InlineData("keywords", "fore", false)]
    [InlineData("Description", "synapic live acceptance 2026-10-23", true)] // the tag name must match too
    public void A_value_is_matched_whole_and_case_insensitively(
        string keyFragment, string value, bool expected)
    {
        Assert.Equal(expected, DaminionApiClient.HasValue(Pairs(LiveShapeJson), keyFragment, value));
    }

    [Fact]
    public void An_older_flat_payload_is_still_verified()
    {
        // Not every server (and not every stand-in for one) answers in the
        // nested 11.x shape: a flat record names the tag directly. The verifier
        // kept that reader as the fallback, so the fix for 11.x did not turn
        // every older write into "not found".
        const string flat = """
        {"Category":"Nature","Keyword":"tree","Description":"A forest at dawn"}
        """;

        var missing = DaminionApiClient.FindMissingFields(
            Pairs(flat),
            category: "Nature",
            keywords: new[] { "tree" },
            description: "A forest at dawn");

        Assert.Empty(missing);
    }

    [Fact]
    public void The_nested_shape_is_never_reinterpreted_through_its_member_names()
    {
        // A payload with the nested shape must not gain flat pairs like
        // "propertyName" -> "Keywords": the verifier would then match the name
        // of the tag instead of its value.
        var pairs = Pairs(LiveShapeJson);
        Assert.DoesNotContain(pairs, p => p.Key is "propertyName" or "propertyValue" or "id" or "hashCode");
    }

    [Fact]
    public void The_multi_value_array_is_read_even_though_property_value_is_empty()
    {
        // The failure mode this pins: the Keywords tag carries no single value,
        // only a values array. A reader that skips the array sees "no keywords".
        var pairs = Pairs(LiveShapeJson);
        var keywords = pairs
            .Where(p => p.Key.Equals("Keywords", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value)
            .ToArray();

        Assert.Equal(new[] { "synapic-live-2026-10-23", "forest" }, keywords);
    }
}
