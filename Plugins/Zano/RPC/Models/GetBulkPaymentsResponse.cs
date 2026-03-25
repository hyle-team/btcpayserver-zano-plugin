using System.Collections.Generic;

using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetBulkPaymentsResponse
    {
        [JsonProperty("payments")] public List<Payment> Payments { get; set; }
    }

    public class Payment
    {
        [JsonProperty("amount")] public long Amount { get; set; }
        [JsonProperty("block_height")] public long BlockHeight { get; set; }
        [JsonProperty("payment_id")] public string PaymentId { get; set; }
        [JsonProperty("tx_hash")] public string TxHash { get; set; }
        [JsonProperty("unlock_time")] public long UnlockTime { get; set; }
    }
}
