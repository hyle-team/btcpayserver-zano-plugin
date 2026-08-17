namespace BTCPayServer.Plugins.Zano.Payments
{
    public class ZanoPaymentData
    {
        public string PaymentId { get; set; }
        public long BlockHeight { get; set; }
        public long ConfirmationCount { get; set; }
        public string TransactionId { get; set; }
        public long? InvoiceSettledConfirmationThreshold { get; set; }
        public long LockTime { get; set; } = 0;
        public string AssetId { get; set; }

        // Count of consecutive reconciliation passes where the wallet history omitted
        // this payment and the daemon's targeted get_tx_details lookup confirmed that
        // the transaction is absent from both chain and mempool. Reset when either
        // source sees the transaction again. At the threshold the payment becomes
        // Unaccounted (the invoice itself is never rewritten — BTCPay has no valid
        // path out of Settled; the listener logs critical and relies on the payment
        // row for the merchant-visible signal).
        public int MissingPollCount { get; set; } = 0;
    }
}
