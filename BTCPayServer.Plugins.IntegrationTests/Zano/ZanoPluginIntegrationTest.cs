using System.Globalization;

using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano.Payments;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Tests.Mocks;

using Xunit;

namespace BTCPayServer.Plugins.IntegrationTests.Zano;

public class ZanoPluginIntegrationTest(ITestOutputHelper helper) : ZanoIntegrationTestBase(helper)
{
    [Fact]
    public async Task ShouldEnableZanoPluginSuccessfully()
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        await s.StartAsync();
        await IntegrationTestUtils.WaitForZanoAsync(s);

        if (s.Server.PayTester.MockRates)
        {
            var rateProviderFactory = s.Server.PayTester.GetService<RateProviderFactory>();
            Assert.Contains(s.Server.PayTester.GetService<IEnumerable<IRateProvider>>(),
                provider => provider.RateSourceInfo.Id == "zanocoinpaprika");
            rateProviderFactory.Providers.Clear();

            var zanoRates = new MockRateProvider();
            zanoRates.ExchangeRates.Add(new PairRate(CurrencyPair.Parse("ZANO_USD"), new BidAsk(4.2m)));
            rateProviderFactory.Providers.Add("zanocoinpaprika", zanoRates);

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
        var zanoDue = await s.Page.EvaluateAsync<string>(
            "async invoiceId => (await (await fetch(`/i/${invoiceId}/status?paymentMethodId=ZANO-CHAIN`)).json()).due",
            invoiceId);
        Assert.Equal(1m, decimal.Parse(zanoDue, CultureInfo.InvariantCulture));

        // Select confirmation time to 0
        await s.Page.GoBackAsync();
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();
        await s.Page.SelectOptionAsync("#SettlementConfirmationThresholdChoice", "1");
        await s.Page.ClickAsync("#SaveButton");

    }

    [Fact]
    public async Task ShouldReturnToAssetsAfterDisablingPaymentMethod()
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        await s.StartAsync();
        await IntegrationTestUtils.WaitForZanoAsync(s);
        await s.RegisterNewUser(true);
        await s.CreateNewStore(preferredExchange: "Kraken");
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();
        await s.Page.CheckAsync("#Enabled");
        await s.Page.ClickAsync("#SaveButton");

        await s.Page.UncheckAsync("#Enabled");
        await s.Page.ClickAsync("#SaveButton");

        Assert.EndsWith($"/stores/{s.StoreId}/zano/assets", s.Page.Url);
        var enabled = s.Page.Locator("input[name='WhitelistedCryptoCodes'][value='ZANO']");
        Assert.False(await enabled.IsCheckedAsync());
        await enabled.CheckAsync();
        await s.Page.ClickAsync("#SaveAssetWhitelist");
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();
        Assert.True(await s.Page.IsCheckedAsync("#Enabled"));
    }

    [Fact]
    public async Task ShouldLeaveSharedInvoiceAmountPolicyToCore()
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        await s.StartAsync();
        await IntegrationTestUtils.WaitForZanoAsync(s);
        await s.RegisterNewUser(true);
        await s.CreateNewStore(preferredExchange: "Kraken");
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();
        await s.Page.CheckAsync("#Enabled");
        await s.Page.ClickAsync("#SaveButton");
        var stores = s.Server.PayTester.StoreRepository;
        var store = await stores.FindStore(s.StoreId);
        Assert.NotNull(store);
        var settings = store.GetStoreBlob();
        settings.AllowZeroAmountInvoices = true;
        store.SetStoreBlob(settings);
        await stores.UpdateStore(store);

        var id = await s.CreateInvoice(amount: -5, currency: "USD");
        var invoice = await s.Server.PayTester.InvoiceRepository.GetInvoice(id);
        Assert.NotNull(invoice);

        Assert.Equal(0m, invoice.Price);
        Assert.Empty(invoice.GetPaymentPrompts());
    }

    [Fact]
    public async Task ShouldDisplayConfirmationPolicyInPaymentDetails()
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        await s.StartAsync();
        await IntegrationTestUtils.WaitForZanoAsync(s);
        await s.RegisterNewUser(true);
        await s.CreateNewStore();
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();
        await s.Page.CheckAsync("#Enabled");
        await s.Page.ClickAsync("#SaveButton");
        var invoiceId = await s.CreateInvoice(amount: 4, currency: "ZANO");
        var repository = s.Server.PayTester.GetService<InvoiceRepository>();
        var invoice = await repository.GetInvoice(invoiceId);
        var handlers = s.Server.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var handler = (ZanoPaymentMethodHandler)handlers[PaymentTypes.CHAIN.GetPaymentMethodId("ZANO")];
        var paymentPrompt = invoice.GetPaymentPrompt(handler.PaymentMethodId);
        Assert.NotNull(paymentPrompt);
        var prompt = handler.ParsePaymentPromptDetails(paymentPrompt.Details);
        var paymentService = s.Server.PayTester.GetService<PaymentService>();
        var cases = new[]
        {
            (Required: 0L, Received: 0L, Expected: "No confirmations required"),
            (Required: 0L, Received: 5L, Expected: "No confirmations required"),
            (Required: 1L, Received: 0L, Expected: "0 / 1"),
            (Required: 1L, Received: 5L, Expected: "1 / 1")
        };

        foreach (var (required, received, expected) in cases)
        {
            var transactionId = Guid.NewGuid().ToString("N");
            var payment = new PaymentData
            {
                Id = transactionId,
                InvoiceDataId = invoiceId,
                Currency = "ZANO",
                Amount = 1m,
                Created = DateTimeOffset.UtcNow,
                Status = received >= required ? PaymentStatus.Settled : PaymentStatus.Processing
            }.Set(invoice, handler, new ZanoPaymentData
            {
                PaymentId = prompt.PaymentId,
                TransactionId = transactionId,
                ConfirmationCount = received,
                InvoiceSettledConfirmationThreshold = required
            });
            Assert.NotNull(await paymentService.AddPayment(payment));
            await s.GoToInvoice(invoiceId);
            var row = s.Page.Locator("tr").Filter(new() { HasTextString = transactionId });
            Assert.Equal(expected, await row.Locator("td").Nth(3).InnerTextAsync());
        }
    }

    [Fact]
    public async Task ShouldReportPreOpenedWalletOnStartup()
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        await s.StartAsync();
        await IntegrationTestUtils.WaitForZanoAsync(s);
        await s.RegisterNewUser(true);
        await s.CreateNewStore();
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();

        Assert.Contains("Wallet RPC available: True", await s.Page.ContentAsync());
    }
}