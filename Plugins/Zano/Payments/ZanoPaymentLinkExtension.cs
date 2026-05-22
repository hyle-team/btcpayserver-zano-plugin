#nullable enable
using System.Globalization;

using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;

using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Zano.Payments
{
    public class ZanoPaymentLinkExtension : IPaymentLinkExtension
    {
        private readonly ZanoSpecificBtcPayNetwork _network;

        public ZanoPaymentLinkExtension(PaymentMethodId paymentMethodId, ZanoSpecificBtcPayNetwork network)
        {
            PaymentMethodId = paymentMethodId;
            _network = network;
        }
        public PaymentMethodId PaymentMethodId { get; }

        public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
        {
            // Canonical Zano deeplink (https://docs.zano.org/docs/use/deeplinks/)
            // accepted by both the desktop wallet (qt-daemon) and the mobile wallet.
            // Cake Wallet only parses the BIP21-style form — see GetCakePaymentLink
            // (used for the QR encoding and a secondary "Open in Cake Wallet" button).
            var due = prompt.Calculate().Due.ToString(CultureInfo.InvariantCulture);
            var uri = $"{_network.UriScheme}:action=send&address={prompt.Destination}&amount={due}";
            if (!_network.IsNative && !string.IsNullOrEmpty(_network.AssetId))
            {
                uri += $"&asset_id={_network.AssetId}";
            }
            return uri;
        }

        // BIP21/Monero-style form: zano:{address}?tx_amount={amount}[&asset_id={id}]
        // Cake Wallet, Zano mobile and Edge all parse this form in their QR/URI
        // handlers; we use it for the QR code so scanning works in any wallet.
        public string GetCakePaymentLink(PaymentPrompt prompt)
        {
            var due = prompt.Calculate().Due.ToString(CultureInfo.InvariantCulture);
            var uri = $"{_network.UriScheme}:{prompt.Destination}?tx_amount={due}";
            if (!_network.IsNative && !string.IsNullOrEmpty(_network.AssetId))
            {
                uri += $"&asset_id={_network.AssetId}";
            }
            return uri;
        }
    }
}