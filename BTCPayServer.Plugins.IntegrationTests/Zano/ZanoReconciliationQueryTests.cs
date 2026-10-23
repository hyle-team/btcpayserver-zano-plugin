using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Services.Invoices;

using Microsoft.Extensions.Hosting;

using Xunit;

namespace BTCPayServer.Plugins.IntegrationTests.Zano;

public class ZanoReconciliationQueryTests(ITestOutputHelper helper) : ZanoIntegrationTestBase(helper)
{
    [Fact]
    public async Task SelectsOldMarkedPaymentsWithTheCoreJsonbMapping()
    {
        await using var server = CreateServerTester(newDb: true);
        await server.StartAsync();
        var repository = server.PayTester.GetService<InvoiceRepository>();
        var now = DateTimeOffset.UtcNow;
        var method = PaymentTypes.CHAIN.GetPaymentMethodId("ZANO");
        using (var db = repository.DbContextFactory.CreateContext())
        {
            foreach (var (id, paymentMethod, status, marked) in new[]
            {
                ("marked", method.ToString(), "Settled", true),
                ("unmarked", method.ToString(), "Settled", false),
                ("other-method", "BTC-CHAIN", "Settled", true),
                ("unsettled", method.ToString(), "New", true)
            })
            {
                var invoice = new InvoiceData
                {
                    Id = id,
                    Created = now.AddDays(-40),
                    Status = status,
                    Blob2 = "{}"
                };
                db.Payments.Add(new PaymentData
                {
                    Id = id,
                    InvoiceData = invoice,
                    Created = invoice.Created,
                    PaymentMethodId = paymentMethod,
                    Status = PaymentStatus.Settled,
                    Blob2 = marked ? "{\"details\":{\"reconciliationActive\":true}}" : "{}"
                });
            }
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var listener = server.PayTester.GetService<IEnumerable<IHostedService>>().OfType<ZanoListener>().Single();

        var selected = await listener.SelectReconciliationInvoiceIds(method, now, TestContext.Current.CancellationToken);

        Assert.Equal("marked", Assert.Single(selected));
    }
}