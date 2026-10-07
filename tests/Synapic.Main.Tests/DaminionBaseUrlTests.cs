using Synapic.Avalonia.Services.Daminion;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The Step 1 server URL is free-text user input. A URL without a scheme
/// ("damserver.local") used to surface as "Invalid URI: The format of the URI
/// could not be determined." from deep inside the HTTP stack; NormalizeBaseUrl
/// must accept the natural inputs and reject the unusable ones with a clear
/// message.
/// </summary>
public class DaminionBaseUrlTests
{
    [Theory]
    [InlineData("damserver.local", "http://damserver.local")]
    [InlineData("damserver.local:8080", "http://damserver.local:8080")]
    [InlineData("192.168.1.10", "http://192.168.1.10")]
    [InlineData("192.168.1.10:8080", "http://192.168.1.10:8080")]
    [InlineData("http://damserver.local", "http://damserver.local")]
    [InlineData("https://damserver.local", "https://damserver.local")]
    [InlineData("  damserver.local/  ", "http://damserver.local")]
    [InlineData("http://damserver.local/", "http://damserver.local")]
    [InlineData("http://damserver.local/catalog/", "http://damserver.local/catalog")]
    public void Natural_inputs_normalize(string input, string expected)
    {
        Assert.Equal(expected, DaminionApiClient.NormalizeBaseUrl(input));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://damserver.local")]
    public void Unusable_inputs_throw_with_guidance(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => DaminionApiClient.NormalizeBaseUrl(input));
        Assert.Contains("not a valid Daminion server URL", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_inputs_report_the_url_is_empty(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => DaminionApiClient.NormalizeBaseUrl(input));
        Assert.Contains("URL is empty", ex.Message);
    }
}
