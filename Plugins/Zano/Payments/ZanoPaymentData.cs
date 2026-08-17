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

        // Unix seconds of the FIRST time this payment reached Settled. Anchors the
        // post-settlement reconciliation window: protection must start when the
        // merchant could first have shipped, not when the payment was first seen
        // (settlement can lag detection by days under high thresholds or time
        // locks). Never cleared once set, so a downgrade/restore cycle keeps the
        // original anchor. Null on rows written before this field existed — those
        // fall back to the row's Created time.
        public long? SettledAt { get; set; }
    }
}
