using Microsoft.Extensions.Configuration;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>Security review 2026-11-26, A02 F2: warn about plain-HTTP endpoints beyond loopback.</summary>
public class PlainHttpEndpointsTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5443")]
    [InlineData("http://*:5443")]
    [InlineData("http://+:5443")]
    [InlineData("http://[::]:5443")]
    [InlineData("http://192.168.1.1:5443")]
    [InlineData("HTTP://myria.example:80")]
    public void PlainHttpBeyondLoopback_IsFlagged(string url) =>
        Assert.True(PlainHttpEndpoints.IsPlainAndNotLoopback(url));

    [Theory]
    [InlineData("http://127.0.0.1:5443")]
    [InlineData("http://localhost:5443")]
    [InlineData("http://[::1]:5443")]
    [InlineData("http://127.0.0.2:5443")]
    [InlineData("https://0.0.0.0:5000")]
    public void LoopbackOrHttps_IsNotFlagged(string url) =>
        Assert.False(PlainHttpEndpoints.IsPlainAndNotLoopback(url));

    [Fact]
    public void ReadsKestrelEndpointsAndUrls()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5443",
            ["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:5000",
            ["Kestrel:Endpoints:Local:Url"] = "http://127.0.0.1:5444",
            ["urls"] = "http://*:8080;http://localhost:8081",
        }).Build();
        Assert.Equal(["http://0.0.0.0:5443", "http://*:8080"], PlainHttpEndpoints.ReachableFromNetwork(config));
    }

    [Fact]
    public void IkarusTargetConfig_HasNoWarning()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:5443",
            ["Kestrel:Endpoints:Https:Url"] = "https://127.0.0.1:5000",
        }).Build();
        Assert.Empty(PlainHttpEndpoints.ReachableFromNetwork(config));
    }
}
