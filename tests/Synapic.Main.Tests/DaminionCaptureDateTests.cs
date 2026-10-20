using Synapic.Main.Services.Daminion;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Reading a catalog item's capture date out of its metadata payload. This is
/// what keeps "keep oldest / keep newest" working on a catalog scan that
/// fingerprints the server's preview instead of downloading the original: a
/// preview carries no EXIF, and the media-item record itself has no date field,
/// so the date comes from <c>ItemData/GetAll</c> — for the rows the user is
/// reviewing, not for the whole catalog.
/// </summary>
public class DaminionCaptureDateTests
{
    private static DateTime? Parse(params (string Key, string Value)[] pairs) =>
        DaminionApiClient.ParseCaptureDate(pairs);

    [Fact]
    public void The_nested_item_data_payload_is_read_as_name_value_pairs()
    {
        // Shape observed from /api/ItemData/GetAll on server 11.x: the tag name is
        // `propertyName` and its value `propertyValue`, two levels deep. A reader
        // keyed on the JSON's own member names would only ever find the words
        // "propertyName"/"propertyValue" and never a date.
        const string json = """
        {
          "properties": [
            { "name": "Metadata", "id": "0", "properties": [
                { "propertyName": "Date Taken", "propertyGuid": "g1", "propertyValue": "2017-03-14 09:26:53", "values": null, "isMultiplyValues": false, "propertyType": "DateTime" },
                { "propertyName": "Created", "propertyGuid": "g2", "propertyValue": "2026-01-02 10:00:00", "values": null, "isMultiplyValues": false, "propertyType": "DateTime" }
            ]},
            { "name": "Keywords", "id": "1", "properties": [
                { "propertyName": "Keywords", "propertyGuid": "g3", "propertyValue": "", "values": ["holiday", "beach"], "isMultiplyValues": true, "propertyType": "String" }
            ]}
          ],
          "id": 42,
          "hashCode": 12345
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var pairs = DaminionApiClient.TagPairsFromItemData(doc.RootElement);

        Assert.Contains(pairs, p => p.Key == "Date Taken" && p.Value == "2017-03-14 09:26:53");
        Assert.Contains(pairs, p => p.Key == "Keywords" && p.Value == "holiday");
        Assert.Equal(
            new DateTime(2017, 3, 14, 9, 26, 53, DateTimeKind.Local).ToUniversalTime(),
            DaminionApiClient.ParseCaptureDate(pairs));
    }

    [Fact]
    public void DateTaken_is_preferred_over_the_import_created_date()
    {
        // A catalog holds both: when the file was imported and when the shutter
        // fired. "Oldest" means the picture, so the taken time must win.
        var taken = new DateTime(2019, 5, 4, 8, 30, 0, DateTimeKind.Local);
        var imported = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Local);

        var parsed = Parse(
            ("Created", imported.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Date Taken", taken.ToString("yyyy-MM-dd HH:mm:ss")));

        Assert.Equal(taken.ToUniversalTime(), parsed);
    }

    [Theory]
    [InlineData("Date Taken")]
    [InlineData("DateTimeOriginal")]
    [InlineData("date_taken")]
    [InlineData("Original Date")]
    public void The_capture_tag_is_recognised_under_its_usual_names(string key)
    {
        var expected = new DateTime(2021, 11, 9, 16, 45, 0, DateTimeKind.Local);
        var parsed = Parse((key, expected.ToString("yyyy-MM-dd HH:mm:ss")));
        Assert.Equal(expected.ToUniversalTime(), parsed);
    }

    [Theory]
    [InlineData("2020-02-29T13:14:15")]
    [InlineData("2020-02-29 13:14:15")]
    [InlineData("2020-02-29")]
    public void Common_date_shapes_parse(string value)
    {
        var parsed = Parse(("Date Taken", value));
        Assert.NotNull(parsed);
        Assert.Equal(2020, parsed!.Value.ToLocalTime().Year);
        Assert.Equal(2, parsed.Value.ToLocalTime().Month);
        Assert.Equal(29, parsed.Value.ToLocalTime().Day);
    }

    [Theory]
    [InlineData("2025.12.27.11.23.52")]
    [InlineData("2025.12.27.11.23")]
    [InlineData("2025.12.27")]
    public void The_servers_dotted_rendering_parses(string value)
    {
        // What a live catalog actually returns for "Creation Datetime": neither
        // DateTime.TryParse nor ISO-8601 handles this shape.
        var parsed = Parse(("Creation Datetime", value));
        Assert.NotNull(parsed);
        // Stored as UTC, so compare the wall-clock day it came back as.
        Assert.Equal(new DateTime(2025, 12, 27), parsed!.Value.ToLocalTime().Date);
    }

    [Fact]
    public void Creation_Datetime_is_recognised_as_a_capture_date()
    {
        // The tag a real catalog carries (there is no "Date Taken" on it).
        var parsed = Parse(("Creation Datetime", "2019.06.15.08.05.00"));
        Assert.Equal(new DateTime(2019, 6, 15, 8, 5, 0, DateTimeKind.Local).ToUniversalTime(), parsed);
    }

    [Fact]
    public void An_offset_is_honoured()
    {
        var parsed = Parse(("Date Taken", "2020-02-29T13:14:15+02:00"));
        Assert.Equal(new DateTime(2020, 2, 29, 11, 14, 15, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void A_modification_or_expiry_date_is_never_used_as_the_capture_date()
    {
        // "Modified" says when the file was touched, not when it was taken; using
        // it would make "keep oldest" keep whichever copy was edited last.
        Assert.Null(Parse(("Modified", "2024-01-01 10:00:00")));
        Assert.Null(Parse(("Expiration Date", "2030-01-01 10:00:00")));
        Assert.Null(Parse(("Keywords", "holiday"), ("Description", "a beach")));
    }

    [Fact]
    public void An_empty_or_unparseable_value_is_null_rather_than_an_exception()
    {
        Assert.Null(Parse(("Date Taken", "")));
        Assert.Null(Parse(("Date Taken", "not a date")));
        Assert.Null(Parse());
    }

    [Fact]
    public void The_first_of_several_values_for_the_same_tag_wins()
    {
        // The payload repeats a tag across its nested shapes; the first non-empty
        // reading is the one the layout listed.
        var parsed = Parse(
            ("Date Taken", ""),
            ("Date Taken", "2018-07-06 07:08:09"));
        Assert.Equal(new DateTime(2018, 7, 6, 7, 8, 9, DateTimeKind.Local).ToUniversalTime(), parsed);
    }
}
