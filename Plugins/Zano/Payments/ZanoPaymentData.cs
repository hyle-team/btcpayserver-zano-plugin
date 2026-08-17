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

        // Unix seconds when the payment last became Unaccounted (a lost transaction).
        // Bounds how long an Unaccounted row stays eligible for daemon recovery.
        public long? UnaccountedAt { get; set; }

        // ---- Merchant-alert outbox -------------------------------------------------
        // Every reconciliation transition that a merchant must hear about is recorded
        // HERE, in the same write as the state change, and delivered AFTER that write
        // has committed. A pending flag stays set until BTCPay's notification store has
        // accepted the alert, and is retried on later passes regardless of what the
        // payment's status has become since — so a crash, a transient store failure,
        // or a wallet-driven recovery landing before the retry cannot lose an alert.
        // Episode counters make the notification identifiers deterministic across
        // retries (same episode → de-duplicated) and distinct across genuine
        // repetitions (lost → restored → lost again alerts twice).

        // Number of times this payment has become Unaccounted. 0 = never.
        public int LossEpisode { get; set; }
        public bool LossAlertPending { get; set; }

        // Set when the payment leaves Unaccounted (daemon- or wallet-driven recovery).
        // Its identifier is tied to LossEpisode: it announces the end of THAT loss.
        public bool RestoreAlertPending { get; set; }

        // Number of times this payment fell below its confirmation policy while its
        // invoice was already Settled (reorg). 0 = never.
        public int RegressionEpisode { get; set; }
        public bool RegressionAlertPending { get; set; }

        public bool HasPendingAlert => LossAlertPending || RestoreAlertPending || RegressionAlertPending;
    }
}
