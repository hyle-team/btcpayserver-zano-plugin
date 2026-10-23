using System.Net;
using System.Text;

using BTCPayServer.Plugins.Zano.Services;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano;

public class ZanoCoinPaprikaRateProviderTests
{
    [Fact]
    public async Task ReturnsNativeUsdQuote()
    {
        using var client = new HttpClient(new StubHandler("""{ "quotes": { "USD": { "price": 4.25 } } }"""));
        var provider = new ZanoCoinPaprikaRateProvider(client);
        var rate = Assert.Single(await provider.GetRatesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ZANO_USD", rate.CurrencyPair.ToString());
        Assert.Equal(4.25m, rate.BidAsk.Bid);
        Assert.Equal(4.25m, rate.BidAsk.Ask);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"quotes\": { \"USD\": { \"price\": null } } }")]
    [InlineData("{ \"quotes\": { \"USD\": { \"price\": 0 } } }")]
    [InlineData("{ \"quotes\": { \"USD\": { \"price\": -1 } } }")]
    public async Task RejectsMissingOrNonPositivePrices(string json)
    {
        using var client = new HttpClient(new StubHandler(json));
        var provider = new ZanoCoinPaprikaRateProvider(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRatesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsUnsuccessfulResponses()
    {
        using var client = new HttpClient(new StubHandler("{}", HttpStatusCode.TooManyRequests));
        var provider = new ZanoCoinPaprikaRateProvider(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetRatesAsync(TestContext.Current.CancellationToken));
    }

    private class StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.coinpaprika.com/v1/tickers/zano-zano", request.RequestUri?.AbsoluteUri);
            Assert.Equal("BTCPayServer-Zano", request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}