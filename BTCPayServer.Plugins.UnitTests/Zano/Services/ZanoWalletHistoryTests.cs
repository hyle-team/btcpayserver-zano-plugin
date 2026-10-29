using System.Net;

using BTCPayServer.Logging;
using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.RPC;
using BTCPayServer.Plugins.Zano.RPC.Models;
using BTCPayServer.Plugins.Zano.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Services;

public class ZanoWalletHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeeingOnePaymentIdDoesNotHideAnOlderReceipt(bool wrongAsset)
    {
        using var transport = new HistoryTransport(offset => Reply(offset == 0
            ? new[] { Row("newer", "invoice-pid", wrongAsset ? "other-asset" : "invoice-asset") }
                .Concat(Enumerable.Range(1, 199).Select(i => Row($"other-{i}", "other-pid"))).ToList()
            : [Row("older", "invoice-pid", "invoice-asset")]));
        using var client = new HttpClient(transport);

        var (transfers, complete) = await ReadAsync(client, TestContext.Current.CancellationToken);

        Assert.Contains(transfers, tx => tx.TxHash == "older");
        Assert.Equal(2, transfers.Count(tx => tx.SubtransfersByPid.Any(p => p.PaymentId == "invoice-pid")));
        Assert.Equal(new[] { 0, 200 }, transport.Offsets);
        Assert.True(complete);
    }

    [Fact]
    public async Task FullPagesRespectTheScanCapAndRemainIncomplete()
    {
        using var transport = new HistoryTransport(offset => Reply(Enumerable.Range(offset, 200)
            .Select(i => Row($"tx-{i}", "invoice-pid")).ToList()));
        using var client = new HttpClient(transport);

        var (transfers, complete) = await ReadAsync(client, TestContext.Current.CancellationToken);

        Assert.Equal(2000, transfers.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 200), transport.Offsets);
        Assert.False(complete);
    }

    [Fact]
    public async Task FailedLaterPagePreservesThePrefixWithoutClaimingCompletion()
    {
        using var transport = new HistoryTransport(offset => offset == 0
            ? Reply(Enumerable.Range(0, 200).Select(i => Row($"tx-{i}", "invoice-pid")).ToList())
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"error\":{\"code\":-1,\"message\":\"Unavailable\"}}")
            });
        using var client = new HttpClient(transport);

        var (transfers, complete) = await ReadAsync(client, TestContext.Current.CancellationToken);

        Assert.Equal(200, transfers.Count);
        Assert.Equal(new[] { 0, 200 }, transport.Offsets);
        Assert.False(complete);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var transport = new HistoryTransport(_ => Reply([]));
        using var client = new HttpClient(transport);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(client, cancelled.Token));
    }

    private static Task<(List<ZanoTransfer> Transfers, bool Complete)> ReadAsync(
        HttpClient client, CancellationToken cancellationToken = default)
    {
        var listener = new ZanoListener(null, new EventAggregator(new Logs()), null, new ZanoConfiguration(),
            null, NullLogger<ZanoListener>.Instance, null, null, null);
        return listener.FetchWalletTransfersAsync(new JsonRpcClient(new Uri("http://wallet.invalid"), client),
            "ZANO", cancellationToken);
    }

    private static ZanoTransfer Row(string hash, string paymentId, string asset = "invoice-asset") => new()
    {
        TxHash = hash,
        Height = 1,
        SubtransfersByPid =
        [
            new()
            {
                PaymentId = paymentId,
                Subtransfers = [new() { Amount = 1, AssetId = asset, IsIncome = true }]
            }
        ]
    };

    private static HttpResponseMessage Reply(List<ZanoTransfer> transfers) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonConvert.SerializeObject(new { result = new GetRecentTxsAndInfo2Response { Transfers = transfers } }))
    };

    private sealed class HistoryTransport(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<int> Offsets { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var offset = payload["params"]["offset"].Value<int>();
            Offsets.Add(offset);
            return response(offset);
        }
    }
}