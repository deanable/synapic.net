using System.Text.Json;
using Synapic.Avalonia.Services.Daminion;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Settings/GetCatalogGuid has no documented response shape and differs
/// between server builds. Daminion 11 answers with the standard envelope
/// <c>{"data":"&lt;guid&gt;","error":null,"success":true,"errorCode":0}</c>,
/// which the previous parser rejected - so every connect logged
/// "Catalog guid: no usable value" even though the GUID was right there.
/// </summary>
public class DaminionCatalogGuidTests
{
    private static JsonElement Parse(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Bare_guid_string_is_read()
    {
        Assert.Equal(
            "7791b6d4-df38-405c-9f03-601365a253f2",
            DaminionApiClient.ExtractCatalogGuid(
                Parse("\"7791b6d4-df38-405c-9f03-601365a253f2\"")));
    }

    [Fact]
    public void Daminion_11_envelope_reads_the_data_field()
    {
        // Payload captured from the live server (the shape that warned).
        const string json =
            "{\"data\":\"7791b6d4-df38-405c-9f03-601365a253f2\",\"error\":null,\"success\":true,\"errorCode\":0}";

        Assert.Equal(
            "7791b6d4-df38-405c-9f03-601365a253f2",
            DaminionApiClient.ExtractCatalogGuid(Parse(json)));
    }

    [Fact]
    public void Well_known_object_keys_are_still_preferred()
    {
        Assert.Equal(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            DaminionApiClient.ExtractCatalogGuid(
                Parse("{\"guid\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"data\":\"ignored\"}")));
    }

    [Fact]
    public void Data_wrapping_an_object_is_unwrapped()
    {
        Assert.Equal(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            DaminionApiClient.ExtractCatalogGuid(
                Parse("{\"data\":{\"catalogGuid\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"}}")));
    }

    [Fact]
    public void Unusable_payloads_return_null()
    {
        Assert.Null(DaminionApiClient.ExtractCatalogGuid(Parse("{\"data\":null,\"success\":true}")));
        Assert.Null(DaminionApiClient.ExtractCatalogGuid(Parse("{\"somethingElse\":1}")));
        Assert.Null(DaminionApiClient.ExtractCatalogGuid(Parse("null")));
        Assert.Null(DaminionApiClient.ExtractCatalogGuid(Parse("\"   \"")));
    }
}
