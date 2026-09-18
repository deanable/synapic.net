using Synapic.Avalonia.Models;
using Xunit;

namespace Synapic.Avalonia.Tests;

public class SessionTests
{
    [Fact]
    public void ValidateForStep2_LocalWithoutPath_RequiresFolder()
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = "";
        var (valid, error) = session.ValidateForStep3(daminionConnected: false);
        Assert.False(valid);
        Assert.Contains("folder", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateForStep3_DaminionNotConnected_Fails()
    {
        var session = new Session();
        session.Datasource.Type = "daminion";
        var (valid, error) = session.ValidateForStep3(daminionConnected: false);
        Assert.False(valid);
        Assert.Contains("Daminion", error);
    }

    [Fact]
    public void ValidateForStep3_LocalWithModelAndPath_Passes()
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = System.IO.Path.GetTempPath();
        session.Engine.ModelId = "LiquidAI/LFM2.5-VL-1.6B";
        var (valid, _) = session.ValidateForStep3(daminionConnected: false);
        Assert.True(valid);
    }

    [Fact]
    public void ResetStats_ClearsCountersAndResults()
    {
        var session = new Session();
        session.TotalItems = 10;
        session.ProcessedItems = 7;
        session.FailedItems = 3;
        session.ResetStats();
        Assert.Equal(0, session.TotalItems);
        Assert.Equal(0, session.ProcessedItems);
        Assert.Equal(0, session.FailedItems);
        Assert.Empty(session.Results);
    }
}
