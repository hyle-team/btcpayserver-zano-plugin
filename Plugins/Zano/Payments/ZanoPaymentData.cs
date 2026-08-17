using System.Collections.Generic;

using Newtonsoft.Json;

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
        // has committed. Each record is immutable: it carries the kind, a durable
        // episode identifier, and a snapshot of the placement/status at the moment of
        // the transition, so a delayed delivery reports what actually happened rather
        // than the row's latest state. Records are delivered in order and removed one
        // by one once BTCPay's notification store has accepted them; retries happen on
        // later passes regardless of what the payment's status has become since — so
        // a crash, a transient store failure, or a wallet-driven recovery landing
        // before the retry cannot lose an alert, and a lost→restored→lost sequence
        // while the store is down keeps all three.

        // Number of times this payment has become Unaccounted. 0 = never. Identifies
        // the loss episode; the matching restore is tied to the same number.
        public int LossEpisode { get; set; }

        // Number of times this payment fell below its confirmation policy while its
        // invoice was already Settled (reorg). 0 = never.
        public int RegressionEpisode { get; set; }

        public List<ZanoPendingAlert> PendingAlerts { get; set; }

        [JsonIgnore]
        public bool HasPendingAlert => PendingAlerts is { Count: > 0 };

        // Durable "this row still needs reconciliation attention" marker, maintained
        // by the listener on every write (see ZanoListener.RefreshReconciliationActive)
        // and queryable server-side via JSONB containment. It lets selection find old
        // rows with live state (a fresh SettledAt on a long-Processing payment, an
        // in-flight miss sequence, an undelivered alert) without a time bound.
        // Serialized only when true (default-value handling), so the containment
        // predicate {"details":{"reconciliationActive":true}} matches exactly the
        // rows that need it.
        public bool ReconciliationActive { get; set; }
    }

    public class ZanoPendingAlert
    {
        // "PaymentLost" | "PaymentRestored" | "ConfirmationsRegressed"
        public string Kind { get; set; }
        // Durable episode identifier, e.g. "L2" (loss/restore #2) or "G1" (regression #1).
        public string Episode { get; set; }
        public long BlockHeight { get; set; }
        public long Confirmations { get; set; }
        // Payment status right after the transition, as text.
        public string Status { get; set; }
        public long QueuedAt { get; set; }
    }
}
