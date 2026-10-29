using System.Net;
using System.Text;

using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano;
using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.Payments;
using BTCPayServer.Plugins.Zano.RPC;
using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;

using Microsoft.Extensions.Hosting;

using Newtonsoft.Json.Linq;

using Xunit;

namespace BTCPayServer.Plugins.IntegrationTests.Zano;

public class ZanoProcessingReconciliationTests(ITestOutputHelper helper) : ZanoIntegrationTestBase(helper)
{
    [Fact]
    public async Task RecordedPaymentOutsideHistorySettlesFromFreshPlacementDespiteCachedPositive()
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        var state = await PrepareAsync(s);
        using var fixture = new ProbeFixture(state.Rpc, "{\"result\":{\"status\":\"OK\",\"tx_info\":{\"keeper_block\":-1}}}");
        var group = Group(s);
        await state.Listener.UpdateAllPaymentStatesForWalletGroupAsync(group, TestContext.Current.CancellationToken);
        Assert.Equal(PaymentStatus.Processing, Assert.Single((await s.Server.PayTester.InvoiceRepository.GetInvoice(state.Invoice.Id)).GetPayments(false)).Status);

        fixture.Daemon.Reply = Confirmed(state.Height);
        var updates = new List<string>();
        using var subscription = s.Server.PayTester.GetService<EventAggregator>().Subscribe<InvoiceNeedUpdateEvent>(e => updates.Add(e.InvoiceId));
        await state.Listener.UpdateAllPaymentStatesForWalletGroupAsync(group, TestContext.Current.CancellationToken);
        Assert.Equal(20, fixture.Wallet.Pages);
        Assert.Equal(2, fixture.Daemon.Probes);
        var after = await s.Server.PayTester.InvoiceRepository.GetInvoice(state.Invoice.Id);
        var payment = Assert.Single(after.GetPayments(false));
        var details = state.Handler.ParsePaymentDetails(payment.Details);
        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.Equal(1m, payment.Value);
        Assert.Equal(state.Invoice.GetPaymentPrompt(state.Handler.PaymentMethodId).Destination, payment.Destination);
        Assert.Equal(state.Height - 10, details.BlockHeight);
        Assert.Equal(10, details.ConfirmationCount);
        Assert.NotNull(details.SettledAt);
        Assert.Equal(0, details.MissingPollCount);
        Assert.Empty(details.PendingAlerts ?? []);
        Assert.Contains(state.Invoice.Id, updates);
        for (var attempt = 0; attempt < 100 && after.Status != InvoiceStatus.Settled; attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            after = await s.Server.PayTester.InvoiceRepository.GetInvoice(state.Invoice.Id);
        }
        Assert.Equal(InvoiceStatus.Settled, after.Status);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":-14,\"message\":\"tx is not found\"}}")]
    [InlineData("{\"result\":null}")]
    [InlineData("{\"result\":{\"status\":\"BUSY\"}}")]
    [InlineData("{\"result\":{\"status\":\"OK\",\"tx_info\":{}}}")]
    [InlineData("{\"result\":{\"status\":\"OK\",\"tx_info\":{\"keeper_block\":-2}}}")]
    public async Task MissingOrUncertainPlacementLeavesProcessingPaymentUntouched(string reply)
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        var state = await PrepareAsync(s);
        var before = Assert.Single(state.Invoice.GetPayments(false));
        using var fixture = new ProbeFixture(state.Rpc, reply);

        await state.Listener.UpdateAllPaymentStatesForWalletGroupAsync(Group(s), TestContext.Current.CancellationToken);

        var after = Assert.Single((await s.Server.PayTester.InvoiceRepository.GetInvoice(state.Invoice.Id)).GetPayments(false));
        Assert.Equal(PaymentStatus.Processing, after.Status);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Destination, after.Destination);
        Assert.True(JToken.DeepEquals(before.Details, after.Details));
        Assert.Equal(1, fixture.Daemon.Probes);
        Assert.Equal(10, fixture.Wallet.Pages);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FreshPlacementStillRespectsHeightAndTimestampLocks(int lockKind)
    {
        await using var s = CreatePlaywrightTester(newDb: true);
        var state = await PrepareAsync(s, lockKind);
        var before = state.Handler.ParsePaymentDetails(Assert.Single(state.Invoice.GetPayments(false)).Details);
        using var fixture = new ProbeFixture(state.Rpc, Confirmed(state.Height));

        await state.Listener.UpdateAllPaymentStatesForWalletGroupAsync(Group(s), TestContext.Current.CancellationToken);

        var after = Assert.Single((await s.Server.PayTester.InvoiceRepository.GetInvoice(state.Invoice.Id)).GetPayments(false));
        var details = state.Handler.ParsePaymentDetails(after.Details);
        Assert.Equal(PaymentStatus.Processing, after.Status);
        Assert.Equal(state.Height - 10, details.BlockHeight);
        Assert.Equal(10, details.ConfirmationCount);
        Assert.Equal(before.LockTime, details.LockTime);
        Assert.Equal(before.AssetId, details.AssetId);
        Assert.Equal(before.PaymentId, details.PaymentId);
        Assert.Equal(before.InvoiceSettledConfirmationThreshold, details.InvoiceSettledConfirmationThreshold);
        Assert.Null(details.SettledAt);
        Assert.Equal(0, details.MissingPollCount);
        Assert.Empty(details.PendingAlerts ?? []);
    }

    private static string Confirmed(long height) =>
        new JObject { ["result"] = new JObject { ["status"] = "OK", ["tx_info"] = new JObject { ["keeper_block"] = height - 10 } } }.ToString();

    private static ZanoWalletGroup Group(PlaywrightTester s) =>
        s.Server.PayTester.GetService<ZanoConfiguration>().GroupByWallet().Single(g => g.CryptoCodes.Contains("ZANO"));

    private static async Task<(ZanoListener Listener, InvoiceEntity Invoice, ZanoPaymentMethodHandler Handler, ZanoRpcProvider Rpc, long Height)> PrepareAsync(PlaywrightTester s, int lockKind = 0)
    {
        var listener = await ZanoReceiptTests.StartAsync(s);
        await s.Server.PayTester.GetService<IEnumerable<IHostedService>>().OfType<ZanoSummaryUpdaterHostedService>().Single().StopAsync(TestContext.Current.CancellationToken);
        var rpc = s.Server.PayTester.GetService<ZanoRpcProvider>();
        var height = rpc.Summaries["ZANO"].CurrentHeight;
        var id = await s.CreateInvoice(amount: 1, currency: "ZANO");
        var invoice = await s.Server.PayTester.InvoiceRepository.GetInvoice(id);
        var handler = (ZanoPaymentMethodHandler)s.Server.PayTester.GetService<PaymentMethodHandlerDictionary>()[PaymentTypes.CHAIN.GetPaymentMethodId("ZANO")];
        var paymentId = handler.ParsePaymentPromptDetails(invoice.GetPaymentPrompt(handler.PaymentMethodId).Details).PaymentId;
        var lockTime = lockKind switch { 1 => height + 20, 2 => DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), _ => 0 };
        await listener.HandlePaymentData("ZANO", 1_000_000_000_000m, paymentId, Guid.NewGuid().ToString("N"), 0, height,
            lockTime, ZanoAssets.NativeAssetId, invoice, [], TestContext.Current.CancellationToken);
        invoice = await s.Server.PayTester.InvoiceRepository.GetInvoice(id);
        Assert.Equal(PaymentStatus.Processing, Assert.Single(invoice.GetPayments(false)).Status);
        return (listener, invoice, handler, rpc, height);
    }

    private sealed class ProbeFixture : IDisposable
    {
        private readonly ZanoRpcProvider _rpc;
        private readonly JsonRpcClient _wallet;
        private readonly JsonRpcClient _daemon;
        private readonly HttpClient _walletClient;
        private readonly HttpClient _daemonClient;
        public HistoryTransport Wallet { get; } = new();
        public DaemonTransport Daemon { get; }

        public ProbeFixture(ZanoRpcProvider rpc, string reply)
        {
            _rpc = rpc;
            _wallet = rpc.WalletRpcClients["ZANO"];
            _daemon = rpc.DaemonRpcClients["ZANO"];
            Daemon = new DaemonTransport(reply);
            _walletClient = new HttpClient(Wallet);
            _daemonClient = new HttpClient(Daemon);
            rpc.WalletRpcClients = rpc.WalletRpcClients.SetItem("ZANO", new JsonRpcClient(new Uri("http://wallet.invalid"), _walletClient));
            rpc.DaemonRpcClients = rpc.DaemonRpcClients.SetItem("ZANO", new JsonRpcClient(new Uri("http://daemon.invalid"), _daemonClient));
        }

        public void Dispose()
        {
            _rpc.WalletRpcClients = _rpc.WalletRpcClients.SetItem("ZANO", _wallet);
            _rpc.DaemonRpcClients = _rpc.DaemonRpcClients.SetItem("ZANO", _daemon);
            _walletClient.Dispose();
            _daemonClient.Dispose();
        }
    }

    private sealed class DaemonTransport(string reply) : HttpMessageHandler
    {
        public string Reply { get; set; } = reply;
        public int Probes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("get_tx_details", payload.Value<string>("method"));
            Probes++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class HistoryTransport : HttpMessageHandler
    {
        public int Pages { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("get_recent_txs_and_info2", payload.Value<string>("method"));
            var offset = payload["params"]!["offset"]!.Value<int>();
            Pages++;
            var transfers = Enumerable.Range(offset, 200).Select(i => new { tx_hash = $"other-{i}", subtransfers_by_pid = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JObject { ["result"] = new JObject { ["transfers"] = JArray.FromObject(transfers) } }.ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}