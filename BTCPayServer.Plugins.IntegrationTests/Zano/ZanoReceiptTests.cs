using System.Net;
using System.Text;

using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano;
using BTCPayServer.Plugins.Zano.Payments;
using BTCPayServer.Plugins.Zano.RPC;
using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;

using Microsoft.Extensions.Hosting;

using Xunit;

namespace BTCPayServer.Plugins.IntegrationTests.Zano;

public class ZanoReceiptTests(ITestOutputHelper helper) : ZanoIntegrationTestBase(helper)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReceiptsKeepAddressAndPublishPostPaymentTotals(int price)
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        var listener = await StartAsync(s);
        var id = await s.CreateInvoice(amount: price, currency: "ZANO");
        var repository = s.Server.PayTester.InvoiceRepository;
        var method = Handler(s);
        var before = Assert.Single(await repository.GetMonitoredInvoices(method.PaymentMethodId, TestContext.Current.CancellationToken));
        var prompt = before.GetPaymentPrompt(method.PaymentMethodId);
        Assert.NotNull(prompt);
        var paymentId = method.ParsePaymentPromptDetails(prompt.Details).PaymentId;
        var tx = Guid.NewGuid().ToString("N");

        var receipt = await s.Server.WaitForEvent<InvoiceEvent>(
            () => listener.HandlePaymentData("ZANO", 1_000_000_000_000m, paymentId, tx,
                1, 100, 0, ZanoAssets.NativeAssetId, before, [], TestContext.Current.CancellationToken),
            e => e.InvoiceId == id && e.Name == InvoiceEvent.ReceivedPayment);
        var after = await repository.GetInvoice(id);

        Assert.Equal(price - 1m, receipt.Invoice.NetDue);
        Assert.Single(receipt.Invoice.GetPayments(false));
        Assert.Same(receipt.Invoice, receipt.Payment.InvoiceEntity);
        var afterPrompt = after.GetPaymentPrompt(method.PaymentMethodId);
        Assert.NotNull(afterPrompt);
        Assert.Equal(prompt.Destination, afterPrompt.Destination);
        Assert.Equal(paymentId, method.ParsePaymentPromptDetails(afterPrompt.Details).PaymentId);
    }

    [Fact]
    public async Task HistoricalReceiptUsesMatchedAddressRatherThanLegacyDestination()
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        var listener = await StartAsync(s);
        var id = await s.CreateInvoice(amount: 2, currency: "ZANO");
        var repository = s.Server.PayTester.InvoiceRepository;
        var invoice = await repository.GetInvoice(id);
        var handler = Handler(s);
        var original = invoice.GetPaymentPrompt(handler.PaymentMethodId);
        Assert.NotNull(original);
        var oldPaymentId = handler.ParsePaymentPromptDetails(original.Details).PaymentId;
        var payments = s.Server.PayTester.GetService<PaymentService>();
        var seed = new PaymentData
        {
            Id = Guid.NewGuid().ToString("N"),
            InvoiceDataId = id,
            Currency = "ZANO",
            Amount = 0.5m,
            Created = DateTimeOffset.UtcNow,
            Status = PaymentStatus.Processing
        }.Set(invoice, handler, new ZanoPaymentData { PaymentId = oldPaymentId, AssetId = ZanoAssets.NativeAssetId });
        var old = Assert.IsType<PaymentEntity>(await payments.AddPayment(seed));
        Assert.True(await s.Server.PayTester.GetService<InvoiceActivator>().ActivateInvoicePaymentMethod(id, handler.PaymentMethodId, true));
        invoice = await repository.GetInvoice(id);
        var current = invoice.GetPaymentPrompt(handler.PaymentMethodId);
        Assert.NotNull(current);
        Assert.NotEqual(original.Destination, current.Destination);
        // Existing deployments may already contain the wrong destination. Do not trust it.
        old.Destination = current.Destination;
        await payments.UpdatePayments([old]);
        invoice = await repository.GetInvoice(id);
        Assert.Equal(current.Destination, Assert.Single(invoice.GetPayments(false)).Destination);
        var tx = Guid.NewGuid().ToString("N");
        var receipt = await s.Server.WaitForEvent<InvoiceEvent>(
            () => listener.HandlePaymentData("ZANO", 1_500_000_000_000m, oldPaymentId, tx,
                1, 100, 0, ZanoAssets.NativeAssetId, invoice, [], TestContext.Current.CancellationToken),
            e => e.InvoiceId == id && e.Name == InvoiceEvent.ReceivedPayment);
        var after = await repository.GetInvoice(id);
        var payment = Assert.Single(after.GetPayments(false), p => p.Id == $"{tx}#{oldPaymentId}");

        Assert.Equal(original.Destination, payment.Destination);
        Assert.Equal(oldPaymentId, handler.ParsePaymentDetails(payment.Details).PaymentId);
        Assert.Equal(1.5m, payment.Value);
        Assert.Equal(0m, receipt.Invoice.NetDue);
        var afterPrompt = after.GetPaymentPrompt(handler.PaymentMethodId);
        Assert.NotNull(afterPrompt);
        Assert.Equal(current.Destination, afterPrompt.Destination);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":-1,\"message\":\"Unavailable\"}}")]
    [InlineData("{\"result\":{}}")]
    [InlineData("not json")]
    public async Task FailedHistoricalLookupPreservesOtherReceiptsAndQueuedUpdates(string response)
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        var listener = await StartAsync(s);
        var id = await s.CreateInvoice(amount: 1, currency: "ZANO");
        var healthyId = await s.CreateInvoice(amount: 1, currency: "ZANO");
        var repository = s.Server.PayTester.InvoiceRepository;
        var handler = Handler(s);
        var invoice = await repository.GetInvoice(id);
        var prompt = invoice.GetPaymentPrompt(handler.PaymentMethodId);
        Assert.NotNull(prompt);
        var oldPaymentId = handler.ParsePaymentPromptDetails(prompt.Details).PaymentId;
        var oldTx = Guid.NewGuid().ToString("N");
        var seed = new PaymentData
        {
            Id = $"{oldTx}#{oldPaymentId}",
            InvoiceDataId = id,
            Currency = "ZANO",
            Amount = 0.25m,
            Created = DateTimeOffset.UtcNow,
            Status = PaymentStatus.Processing
        }.Set(invoice, handler, new ZanoPaymentData { PaymentId = oldPaymentId, AssetId = ZanoAssets.NativeAssetId });
        var payments = s.Server.PayTester.GetService<PaymentService>();
        Assert.NotNull(await payments.AddPayment(seed));
        Assert.True(await s.Server.PayTester.GetService<InvoiceActivator>().ActivateInvoicePaymentMethod(id, handler.PaymentMethodId, true));
        invoice = await repository.GetInvoice(id);
        var updates = new List<(PaymentEntity Payment, InvoiceEntity invoice)>();
        await listener.HandlePaymentData("ZANO", 250_000_000_000m, oldPaymentId, oldTx,
            1, 100, 0, ZanoAssets.NativeAssetId, invoice, updates, TestContext.Current.CancellationToken);
        Assert.Single(updates);
        var rpc = s.Server.PayTester.GetService<ZanoRpcProvider>();
        var originalClients = rpc.WalletRpcClients;
        using var client = new HttpClient(new FailedAddressTransport(response));
        rpc.WalletRpcClients = originalClients.SetItem("ZANO", new JsonRpcClient(new Uri("http://wallet.invalid"), client));
        try
        {
            await listener.HandlePaymentData("ZANO", 1_000_000_000_000m, oldPaymentId, "deferred",
                1, 100, 0, ZanoAssets.NativeAssetId, invoice, updates, TestContext.Current.CancellationToken);
            Assert.Single((await repository.GetInvoice(id)).GetPayments(false));
            Assert.Single(updates);
            var healthy = await repository.GetInvoice(healthyId);
            var healthyPrompt = healthy.GetPaymentPrompt(handler.PaymentMethodId);
            Assert.NotNull(healthyPrompt);
            var currentPaymentId = handler.ParsePaymentPromptDetails(healthyPrompt.Details).PaymentId;
            await listener.HandlePaymentData("ZANO", 1_000_000_000_000m, currentPaymentId, "healthy",
                1, 100, 0, ZanoAssets.NativeAssetId, healthy, updates, TestContext.Current.CancellationToken);
            Assert.Single((await repository.GetInvoice(healthyId)).GetPayments(false));
            await payments.UpdatePayments(updates.Select(p => p.Payment).ToList());
            Assert.Equal(PaymentStatus.Settled, Assert.Single((await repository.GetInvoice(id)).GetPayments(false)).Status);

            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                listener.HandlePaymentData("ZANO", 1_000_000_000_000m, oldPaymentId, "cancelled",
                    1, 100, 0, ZanoAssets.NativeAssetId, invoice, updates, cancelled.Token));
        }
        finally
        {
            rpc.WalletRpcClients = originalClients;
        }
    }

    private sealed class FailedAddressTransport(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }

    private static ZanoPaymentMethodHandler Handler(PlaywrightTester s) =>
        (ZanoPaymentMethodHandler)s.Server.PayTester.GetService<PaymentMethodHandlerDictionary>()[PaymentTypes.CHAIN.GetPaymentMethodId("ZANO")];

    private static async Task<ZanoListener> StartAsync(PlaywrightTester s)
    {
        await s.StartAsync();
        await IntegrationTestUtils.WaitForZanoAsync(s);
        var services = s.Server.PayTester.GetService<IEnumerable<IHostedService>>();
        var listener = services.OfType<ZanoListener>().Single();
        await listener.StopAsync(TestContext.Current.CancellationToken);
        await s.RegisterNewUser(true);
        await s.CreateNewStore(preferredExchange: "Kraken");
        await s.Page.Locator("a.nav-link[href*='zano/ZANO']").ClickAsync();
        await s.Page.CheckAsync("#Enabled");
        await s.Page.SelectOptionAsync("#SettlementConfirmationThresholdChoice", "2");
        await s.Page.ClickAsync("#SaveButton");
        return listener;
    }

}