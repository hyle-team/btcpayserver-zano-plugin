using System.Collections.Generic;
using System.Linq;

using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Zano.Payments
{
    public class ZanoCheckoutModelExtension : ICheckoutModelExtension
    {
        private readonly BTCPayNetworkBase _network;
        private readonly PaymentMethodHandlerDictionary _handlers;
        private readonly IPaymentLinkExtension paymentLinkExtension;

        public ZanoCheckoutModelExtension(
            PaymentMethodId paymentMethodId,
            IEnumerable<IPaymentLinkExtension> paymentLinkExtensions,
            BTCPayNetworkBase network,
            PaymentMethodHandlerDictionary handlers)
        {
            PaymentMethodId = paymentMethodId;
            _network = network;
            _handlers = handlers;
            paymentLinkExtension = paymentLinkExtensions.Single(p => p.PaymentMethodId == PaymentMethodId);
        }
        public PaymentMethodId PaymentMethodId { get; }

        public string Image => _network.CryptoImagePath;
        public string Badge =>
            _network is ZanoSpecificBtcPayNetwork zn && !zn.IsNative
                ? zn.AssetTicker
                : "";

        public void ModifyCheckoutModel(CheckoutModelContext context)
        {
            if (context is not { Handler: ZanoPaymentMethodHandler handler })
            {
                return;
            }
            context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
            var details = context.InvoiceEntity.GetPayments(true)
                    .Select(p => p.GetDetails<ZanoPaymentData>(handler))
                    .Where(p => p is not null)
                    .FirstOrDefault();
            if (details is not null)
            {
                context.Model.ReceivedConfirmations = details.ConfirmationCount;
                context.Model.RequiredConfirmations = (int)ZanoListener.ConfirmationsRequired(details, context.InvoiceEntity.SpeedPolicy);
            }

            // Two URL formats. The link button uses the canonical Zano deeplink
            // (zano:action=send&...) which both Zano wallets understand. The QR
            // encodes the BIP21 form (zano:{addr}?tx_amount=...) which Cake/Edge/
            // Bitcoin.com all parse in their internal QR scanners; the canonical
            // form would only work for the Zano wallets there.
            context.Model.InvoiceBitcoinUrl = paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper);
            context.Model.InvoiceBitcoinUrlQR = paymentLinkExtension is ZanoPaymentLinkExtension zLink
                ? zLink.GetCakePaymentLink(context.Prompt)
                : context.Model.InvoiceBitcoinUrl;
        }
    }
}