using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

public class AgentSettingsTests
{
    [Fact]
    public void Timeout_AgentValueWins()
    {
        var agent = new AgentOptions { TimeoutSeconds = 30 };
        var provider = new ProviderOptions { TimeoutSeconds = 300 };
        Assert.Equal(TimeSpan.FromSeconds(30), AgentSettings.ResolveTimeout(agent, provider));
    }

    [Fact]
    public void Timeout_FallsBackToProvider()
    {
        var agent = new AgentOptions();
        var provider = new ProviderOptions { TimeoutSeconds = 300 };
        Assert.Equal(TimeSpan.FromSeconds(300), AgentSettings.ResolveTimeout(agent, provider));
    }

    [Fact]
    public void Timeout_FallsBackToSdkDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(100), AgentSettings.ResolveTimeout(new AgentOptions(), new ProviderOptions()));
        Assert.Equal(TimeSpan.FromSeconds(100), AgentSettings.ResolveTimeout(new AgentOptions(), null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Timeout_NonPositiveIsIgnored(int value)
    {
        var agent = new AgentOptions { TimeoutSeconds = value };
        var provider = new ProviderOptions { TimeoutSeconds = 300 };
        Assert.Equal(TimeSpan.FromSeconds(300), AgentSettings.ResolveTimeout(agent, provider));
    }
}
