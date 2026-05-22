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

        // Count of consecutive wallet scans where the wallet was reachable, had
        // transfers, but did not include this payment's (txHash, paymentId, assetId).
        // Reset to 0 whenever the payment is re-matched. Used to flip an already-
        // Settled invoice back to Processing after the wallet has clearly dropped
        // the tx (deep reorg, wallet prune) rather than just having missed it once.
        public int MissingPollCount { get; set; } = 0;
    }
}
