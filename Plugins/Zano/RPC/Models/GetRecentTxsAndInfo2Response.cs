using System.Collections.Generic;

using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetRecentTxsAndInfo2Response
    {
        [JsonProperty("transfers")] public List<ZanoTransfer> Transfers { get; set; }
        [JsonProperty("total_transfers")] public long TotalTransfers { get; set; }
        [JsonProperty("last_item_index")] public long LastItemIndex { get; set; }
    }

    public class ZanoTransfer
    {
        [JsonProperty("tx_hash")] public string TxHash { get; set; }
        [JsonProperty("height")] public long Height { get; set; }
        [JsonProperty("unlock_time")] public long UnlockTime { get; set; }
        [JsonProperty("timestamp")] public long Timestamp { get; set; }
        [JsonProperty("fee")] public long Fee { get; set; }
        [JsonProperty("subtransfers_by_pid")] public List<ZanoPaymentIdGroup> SubtransfersByPid { get; set; }
    }

    public class ZanoPaymentIdGroup
    {
        [JsonProperty("payment_id")] public string PaymentId { get; set; }
        [JsonProperty("subtransfers")] public List<ZanoSubtransfer> Subtransfers { get; set; }
    }

    public class ZanoSubtransfer
    {
        // Atomic-unit amounts are represented as `decimal` rather than signed `long` because
        // a 64-bit signed type can't hold valid Confidential-Asset amounts above
        // ~9.22 atomic units when Decimals=18 (ZanoAssetDefinition allows decimals up to 18,
        // making 10 units = 10^19 atomic, which exceeds long.MaxValue and would crash the
        // entire poll cycle on JSON deserialization). decimal carries up to ~7.9e28, so any
        // realistic CA amount fits with room to spare.
        [JsonProperty("amount")] public decimal Amount { get; set; }
        [JsonProperty("asset_id")] public string AssetId { get; set; }
        [JsonProperty("is_income")] public bool IsIncome { get; set; }
    }
}