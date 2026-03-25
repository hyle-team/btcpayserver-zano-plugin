using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using BTCPayServer.Tests.Mocks;

using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.IntegrationTests.Zano;

public class ZanoPluginIntegrationTest(ITestOutputHelper helper) : ZanoAndBitcoinIntegrationTestBase(helper)
{
    [Fact]
    public async Task ShouldEnableZanoPluginSuccessfully()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();

        if (s.Server.PayTester.MockRates)
        {
            var rateProviderFactory = s.Server.PayTester.GetService<RateProviderFactory>();
            rateProviderFactory.Providers.Clear();

            var coinAverageMock = new MockRateProvider();
            coinAverageMock.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("BTC_USD"), new BidAsk(5000m)));
            coinAverageMock.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("BTC_EUR"), new BidAsk(4000m)));
            coinAverageMock.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("ZANO_BTC"), new BidAsk(4500m)));
            rateProviderFactory.Providers.Add("coingecko", coinAverageMock);

            var kraken = new MockRateProvider();
            kraken.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("BTC_USD"), new BidAsk(0.1m)));
            kraken.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("ZANO_USD"), new BidAsk(0.1m)));
            kraken.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("ZANO_BTC"), new BidAsk(0.1m)));
            rateProviderFactory.Providers.Add("kraken", kraken);
        }

        await s.RegisterNewUser(true);
        await s.CreateNewStore(preferredExchange: "Kraken");
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();

        // Enable Zano and configure settlement threshold
        await s.Page.CheckAsync("#Enabled");
        await s.Page.SelectOptionAsync("#SettlementConfirmationThresholdChoice", "2");
        await s.Page.ClickAsync("#SaveButton");

        // Set rate provider
        await s.Page.Locator("#menu-item-General").ClickAsync();
        await s.Page.Locator("#menu-item-Rates").ClickAsync();
        await s.Page.FillAsync("#DefaultCurrencyPairs", "BTC_USD,ZANO_USD,ZANO_BTC");
        await s.Page.SelectOptionAsync("#PrimarySource_PreferredExchange", "kraken");
        await s.Page.Locator("#page-primary").ClickAsync();

        // Generate a new invoice
        await s.Page.Locator("a.nav-link[href*='invoices']").ClickAsync();
        await s.Page.Locator("#page-primary").ClickAsync();
        await s.Page.FillAsync("#Amount", "4.20");
        await s.Page.FillAsync("#BuyerEmail", "zano@zano.org");
        await Task.Delay(TimeSpan.FromSeconds(25)); // wallet-rpc needs some time to sync
        await s.Page.Locator("#page-primary").ClickAsync();

        // View the invoice
        var href = await s.Page.Locator("a[href^='/i/']").GetAttributeAsync("href");
        var invoiceId = href?.Split("/i/").Last();
        await s.Page.Locator($"a[href='/i/{invoiceId}']").ClickAsync();
        await s.Page.ClickAsync("#DetailsToggle");

        // Verify the total fiat amount is $4.20
        var totalFiat = await s.Page
            .Locator("#PaymentDetails-TotalFiat dd.clipboard-button")
            .InnerTextAsync();
        Assert.Equal("$4.20", totalFiat);

        // Select confirmation time to 0
        await s.Page.GoBackAsync();
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();
        await s.Page.SelectOptionAsync("#SettlementConfirmationThresholdChoice", "3");
        await s.Page.ClickAsync("#SaveButton");

        await IntegrationTestUtils.CleanUpAsync(s);
    }

    [Fact]
    public async Task ShouldLoadWalletOnStartUpIfExists()
    {
        await using var s = CreatePlaywrightTester();
        await IntegrationTestUtils.CopyWalletFilesToZanoRpcDirAsync(s, "wallet");
        await s.StartAsync();
        await s.RegisterNewUser(true);
        await s.CreateNewStore();
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();

        var walletRpcIsAvailable = await s.Page
            .Locator("li.list-group-item:text('Wallet RPC available: True')")
            .InnerTextAsync();

        Assert.Contains("Wallet RPC available: True", walletRpcIsAvailable);

        await IntegrationTestUtils.CleanUpAsync(s);
    }

    [Fact]
    public async Task ShouldLoadWalletWithPasswordOnStartUpIfExists()
    {
        await using var s = CreatePlaywrightTester();
        await IntegrationTestUtils.CopyWalletFilesToZanoRpcDirAsync(s, "wallet_password");
        await s.StartAsync();
        await s.RegisterNewUser(true);
        await s.CreateNewStore();
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();

        var walletRpcIsAvailable = await s.Page
            .Locator("li.list-group-item:text('Wallet RPC available: True')")
            .InnerTextAsync();

        Assert.Contains("Wallet RPC available: True", walletRpcIsAvailable);

        await IntegrationTestUtils.CleanUpAsync(s);
    }
}
