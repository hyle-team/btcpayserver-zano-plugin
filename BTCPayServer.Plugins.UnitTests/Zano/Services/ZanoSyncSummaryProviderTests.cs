using BTCPayServer.Logging;
using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.RPC;
using BTCPayServer.Plugins.Zano.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Services;

public class ZanoSyncSummaryProviderTests
{
    [Fact]
    public void UnconfiguredPluginDoesNotPreventHostSync()
    {
        using var http = new HttpClient();
        var provider = new ZanoSyncSummaryProvider(CreateRpc(new ZanoConfiguration(), http));

        Assert.True(provider.AllAvailable());
        Assert.Empty(provider.GetStatuses());
    }

    [Fact]
    public void ConfiguredNetworkIsUnavailableUntilPolled()
    {
        using var http = new HttpClient();
        var provider = new ZanoSyncSummaryProvider(CreateRpc(Configured(), http));

        Assert.False(provider.AllAvailable());
    }

    [Theory]
    [InlineData(false, true, 99L, false)]
    [InlineData(true, false, 99L, false)]
    [InlineData(true, true, 0L, false)]
    [InlineData(true, true, 94L, false)]
    [InlineData(true, true, 95L, true)]
    [InlineData(true, true, 99L, true)]
    public void HostAndPerNetworkStatusFollowPaymentReadiness(bool synced, bool walletAvailable, long walletHeight, bool expected)
    {
        using var http = new HttpClient();
        var rpc = CreateRpc(Configured(), http);
        rpc.Summaries["ZANO"] = new ZanoRpcProvider.ZanoSummary
        {
            Synced = synced,
            WalletAvailable = walletAvailable,
            CurrentHeight = 100,
            WalletHeight = walletHeight
        };
        var provider = new ZanoSyncSummaryProvider(rpc);

        Assert.Equal(expected, rpc.IsAvailable("ZANO"));
        Assert.Equal(expected, provider.AllAvailable());
        Assert.Equal(expected, Assert.Single(provider.GetStatuses()).Available);
    }

    private static ZanoConfiguration Configured() => new()
    {
        ZanoConfigurationItems = new()
        {
            ["ZANO"] = new()
            {
                DaemonRpcUri = new Uri("http://daemon.invalid"),
                InternalWalletRpcUri = new Uri("http://wallet.invalid")
            }
        }
    };

    private static ZanoRpcProvider CreateRpc(ZanoConfiguration configuration, HttpClient http)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(http);
        return new ZanoRpcProvider(configuration, new EventAggregator(new Logs()), factory.Object, NullLogger<JsonRpcClient>.Instance);
    }
}