using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;

using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Zano.Services;

public class ZanoCoinPaprikaRateProvider(HttpClient httpClient) : IRateProvider
{
    public RateSourceInfo RateSourceInfo => new(
        "zanocoinpaprika", "Zano (CoinPaprika)",
        "https://api.coinpaprika.com/v1/tickers/zano-zano");

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RateSourceInfo.Url);
        request.Headers.UserAgent.ParseAdd("BTCPayServer-Zano");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var ticker = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var price = ticker["quotes"]?["USD"]?["price"]?.Value<decimal?>();
        if (price is null or <= 0m)
        {
            throw new InvalidOperationException("CoinPaprika returned no positive ZANO/USD price.");
        }
        return new[] { new PairRate(new CurrencyPair("ZANO", "USD"), new BidAsk(price.Value)) };
    }
}