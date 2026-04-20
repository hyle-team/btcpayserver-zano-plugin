using System.Collections.Generic;

using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetBulkPaymentsRequest
    {
        [JsonProperty("payment_ids")] public List<string> PaymentIds { get; set; }
        [JsonProperty("min_block_height")] public long MinBlockHeight { get; set; }
        [JsonProperty("allow_locked_transactions")] public bool AllowLockedTransactions { get; set; }
        [JsonProperty("asset_id", NullValueHandling = NullValueHandling.Ignore)] public string AssetId { get; set; }
    }
}
