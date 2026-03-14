using BTCPayServer.Plugins.Monero.Configuration;
using BTCPayServer.Plugins.Monero.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using BTCPayServer.Tests.Mocks;

using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.IntegrationTests.Monero;

public class MoneroPluginIntegrationTest(ITestOutputHelper helper) : MoneroAndBitcoinIntegrationTestBase(helper)
{
    private const string PrimaryWalletAddress = "43Pnj6ZKGFTJhaLhiecSFfLfr64KPJZw7MyGH73T6PTDekBBvsTAaWEUSM4bmJqDuYLizhA13jQkMRPpz9VXBCBqQQb6y5L";
    private const string PrimaryViewKey = "1bfa03b0c78aa6bc8292cf160ec9875657d61e889c41d0ebe5c54fd3a2c4b40e";

    [Fact]
    public async Task ShouldEnableMoneroPluginSuccessfully()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();

        try
        {
            if (s.Server.PayTester.MockRates)
            {
                var rateProviderFactory = s.Server.PayTester.GetService<RateProviderFactory>();
                rateProviderFactory.Providers.Clear();

                var coinAverageMock = new MockRateProvider();
                coinAverageMock.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("BTC_USD"), new BidAsk(5000m)));
                coinAverageMock.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("BTC_EUR"), new BidAsk(4000m)));
                coinAverageMock.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("XMR_BTC"), new BidAsk(4500m)));
                rateProviderFactory.Providers.Add("coingecko", coinAverageMock);

                var kraken = new MockRateProvider();
                kraken.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("BTC_USD"), new BidAsk(0.1m)));
                kraken.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("XMR_USD"), new BidAsk(0.1m)));
                kraken.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("XMR_BTC"), new BidAsk(0.1m)));
                rateProviderFactory.Providers.Add("kraken", kraken);
            }

            await s.RegisterNewUser(true);
            await s.CreateNewStore(preferredExchange: "Kraken");
            await s.Page.Locator("a.nav-link[href*='monerolike/XMR']").ClickAsync();
            await s.Page.Locator("input#PrimaryAddress").FillAsync(PrimaryWalletAddress);
            await s.Page.Locator("input#PrivateViewKey").FillAsync(PrimaryViewKey);
            await s.Page.Locator("input#RestoreHeight").FillAsync("0");
            await s.Page.ClickAsync("button[name='command'][value='set-wallet-details']");
            await s.Page.CheckAsync("#Enabled");
            await s.Page.SelectOptionAsync("#SettlementConfirmationThresholdChoice", "2");
            await s.Page.ClickAsync("#SaveButton");
            var classList = await s.Page
                .Locator("svg.icon-checkmark")
                .GetAttributeAsync("class");
            Assert.Contains("text-success", classList);

            // Set rate provider
            await s.Page.Locator("#menu-item-General").ClickAsync();
            await s.Page.Locator("#menu-item-Rates").ClickAsync();
            await s.Page.FillAsync("#DefaultCurrencyPairs", "BTC_USD,XMR_USD,XMR_BTC");
            await s.Page.SelectOptionAsync("#PrimarySource_PreferredExchange", "kraken");
            await s.Page.Locator("#page-primary").ClickAsync();

            // Generate a new invoice
            await s.Page.Locator("a.nav-link[href*='invoices']").ClickAsync();
            await s.Page.Locator("#page-primary").ClickAsync();
            await s.Page.FillAsync("#Amount", "4.20");
            await s.Page.FillAsync("#BuyerEmail", "monero@monero.com");
            await Task.Delay(TimeSpan.FromSeconds(25)); // wallet-rpc needs some time to sync. refactor this later
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

            await s.Page.GoBackAsync();
            await s.Page.Locator("a.nav-link[href*='monerolike/XMR']").ClickAsync();

            // Create a new account label
            await s.Page.FillAsync("#NewAccountLabel", "tst-account");
            await s.Page.ClickAsync("button[name='command'][value='add-account']");

            // Select primary Account Index
            await s.Page.Locator("a.nav-link[href*='monerolike/XMR']").ClickAsync();
            await s.Page.SelectOptionAsync("#AccountIndex", "1");
            await s.Page.ClickAsync("#SaveButton");

            // Verify selected account index
            await s.Page.Locator("a.nav-link[href*='monerolike/XMR']").ClickAsync();
            var selectedValue = await s.Page.Locator("#AccountIndex").InputValueAsync();
            Assert.Equal("1", selectedValue);

            // Select confirmation time to 0
            await s.Page.SelectOptionAsync("#SettlementConfirmationThresholdChoice", "3");
            await s.Page.ClickAsync("#SaveButton");
        }
        finally
        {
            await IntegrationTestUtils.CleanUpAsync(s);
        }
    }

    [Fact]
    public async Task ShouldFailWhenWrongPrimaryAddress()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();

        try
        {
            await s.RegisterNewUser(true);
            await s.CreateNewStore();
            await s.Page.Locator("a.nav-link[href*='monerolike/XMR']").ClickAsync();
            await s.Page.Locator("input#PrimaryAddress")
                .FillAsync("wrongprimaryaddressfSF6ZKGFT7MyGH73T6PTDekBBvsTAaWEUSM4bmJqDuYLizhA13jQkMRPpz9VXBCBqQQb6y5L");
            await s.Page.Locator("input#PrivateViewKey").FillAsync(PrimaryViewKey);
            await s.Page.Locator("input#RestoreHeight").FillAsync("0");
            await s.Page.ClickAsync("button[name='command'][value='set-wallet-details']");
            var errorText = await s.Page
                .Locator("div.validation-summary-errors li")
                .InnerTextAsync();

            Assert.Equal("Could not generate view wallet from keys: Failed to parse public address", errorText);
        }
        finally
        {
            await IntegrationTestUtils.CleanUpAsync(s);
        }
    }

    [Fact]
    public async Task ShouldFailWhenWalletFileAlreadyExists()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();

        try
        {
            MoneroRpcProvider moneroRpcProvider = s.Server.PayTester.GetService<MoneroRpcProvider>();
            await moneroRpcProvider.CreateWalletFromKeys("XMR", "wallet", PrimaryWalletAddress, PrimaryViewKey, "", 0);
            await moneroRpcProvider.CloseWallet("XMR");

            await s.RegisterNewUser(true);
            await s.CreateNewStore();
            await s.Page.Locator("a.nav-link[href*='monerolike/XMR']").ClickAsync();
            await s.Page.Locator("input#PrimaryAddress").FillAsync(PrimaryWalletAddress);
            await s.Page.Locator("input#PrivateViewKey").FillAsync(PrimaryViewKey);
            await s.Page.Locator("input#RestoreHeight").FillAsync("0");
            await s.Page.ClickAsync("button[name='command'][value='set-wallet-details']");
            var errorText = await s.Page
                .Locator("div.validation-summary-errors li")
                .InnerTextAsync();

            Assert.Equal("Could not generate view wallet from keys: Wallet already exists.", errorText);
        }
        finally
        {
            await IntegrationTestUtils.CleanUpAsync(s);
        }
    }

    [Fact]
    public async Task CompleteMigratePasswordFromFile()
    {
        const string walletName = "wallet";
        const string walletPassword = "legacy-password-123";
        string passwordFile;

        {
            await using var tempTester = CreatePlaywrightTester();
            await tempTester.StartAsync();
            var rpcProvider = tempTester.Server.PayTester.GetService<MoneroRpcProvider>();
            var xmrWallet = await rpcProvider.CreateWalletFromKeys(
                "XMR",
                walletName,
                PrimaryWalletAddress,
                PrimaryViewKey,
                walletPassword,
                0);

            Assert.True(xmrWallet.Success);
            await rpcProvider.CloseWallet("XMR");
            string walletDir = rpcProvider.GetWalletDirectory("XMR");
            Assert.NotNull(walletDir);
            passwordFile = Path.Combine(walletDir, "password");
            await File.WriteAllTextAsync(passwordFile, walletPassword);

            await IntegrationTestUtils.CleanUpAsync(tempTester, deleteWalletFiles: false);
        }

        await using var s = CreatePlaywrightTester();
        await s.StartAsync();

        try
        {
            var walletService = s.Server.PayTester.GetService<MoneroWalletService>();
            MoneroWalletState walletState = walletService.GetWalletState();
            Assert.Equal(walletName, walletState.ActiveWalletName);
            Assert.True(walletState.IsConnected);
            await s.RegisterNewUser(true);
            await s.CreateNewStore();
            await s.Page.Locator("a.nav-link[href*='monerolike/XMR']").ClickAsync();

            var walletRpcIsAvailable = await s.Page
                .Locator("li.list-group-item:text('Wallet RPC available: True')")
                .InnerTextAsync();

            Assert.Contains("Wallet RPC available: True", walletRpcIsAvailable);
        }
        finally
        {
            await IntegrationTestUtils.CleanUpAsync(s);
            File.Delete(passwordFile);
        }
    }
}