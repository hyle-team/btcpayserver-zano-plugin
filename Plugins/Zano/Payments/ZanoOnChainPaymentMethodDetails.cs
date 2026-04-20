namespace BTCPayServer.Plugins.Zano.Payments
{
    public class ZanoOnChainPaymentMethodDetails
    {
        public string PaymentId { get; set; }
        public long? InvoiceSettledConfirmationThreshold { get; set; }
        public string AssetId { get; set; }
    }
}
