using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Controllers;
using BTCPayServer.Services.Notifications;

using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.Zano.Services
{
    // Merchant-facing signal for reconciliation events on a SETTLED invoice. BTCPay
    // has no path out of Settled, so the invoice keeps its status; without this the
    // only trace of a vanished payment would be an Unaccounted row (visible only on
    // explicit inspection) and a server log a hosted merchant never sees. Store-scoped
    // dashboard notifications are the channel BTCPay itself uses for "needs a human"
    // conditions such as external payout approvals.
    public class ZanoPaymentReconciliationNotification : BaseNotification
    {
        private const string TYPE = "zano-payment-reconciliation";

        public enum Kind
        {
            PaymentLost,
            ConfirmationsRegressed,
            PaymentRestored
        }

        internal class Handler : NotificationHandler<ZanoPaymentReconciliationNotification>
        {
            private readonly LinkGenerator _linkGenerator;
            private readonly BTCPayServerOptions _options;

            public Handler(LinkGenerator linkGenerator, BTCPayServerOptions options)
            {
                _linkGenerator = linkGenerator;
                _options = options;
            }

            public override string NotificationType => TYPE;

            public override (string identifier, string name)[] Meta
                => new[] { (TYPE, "Zano payment reconciliation") };

            protected override void FillViewModel(ZanoPaymentReconciliationNotification n, NotificationViewModel vm)
            {
                vm.Identifier = n.Identifier;
                vm.Type = n.NotificationType;
                vm.StoreId = n.StoreId;
                vm.Body = n.EventKind switch
                {
                    Kind.PaymentLost =>
                        $"Zano payment on SETTLED invoice {n.InvoiceId} disappeared from the chain and mempool " +
                        $"(tx {Short(n.TransactionId)}). The invoice stays settled; review before fulfilling.",
                    Kind.ConfirmationsRegressed =>
                        $"Zano payment on SETTLED invoice {n.InvoiceId} fell below its confirmation policy " +
                        $"(tx {Short(n.TransactionId)}, {n.Confirmations} conf) — likely a chain reorganization. Review before fulfilling.",
                    Kind.PaymentRestored =>
                        $"Previously lost Zano payment on invoice {n.InvoiceId} is back on chain " +
                        $"(tx {Short(n.TransactionId)}) and has been restored to accounting.",
                    _ => $"Zano reconciliation event on invoice {n.InvoiceId}."
                };
                vm.ActionLink = _linkGenerator.GetPathByAction(
                    nameof(UIInvoiceController.Invoice), "UIInvoice",
                    new { invoiceId = n.InvoiceId }, _options.RootPath);
            }

            private static string Short(string tx)
                => string.IsNullOrEmpty(tx) || tx.Length <= 12 ? tx : tx[..8] + "…" + tx[^4..];
        }

        public string StoreId { get; set; }
        public string InvoiceId { get; set; }
        public string TransactionId { get; set; }
        public long Confirmations { get; set; }
        public Kind EventKind { get; set; }

        // One notification per (invoice, tx, kind): the identifier is what BTCPay's
        // notification store de-duplicates on, so repeated polls cannot spam.
        public override string Identifier => $"{TYPE}_{EventKind}_{InvoiceId}_{TransactionId}";
        public override string NotificationType => TYPE;
    }
}
