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
            var due = prompt.Calculate().Due;
            return $"{_network.UriScheme}:{prompt.Destination}?tx_amount={due.ToString(CultureInfo.InvariantCulture)}";
        }
    }
}