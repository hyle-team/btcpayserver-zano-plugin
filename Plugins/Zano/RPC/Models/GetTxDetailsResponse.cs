using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetTxDetailsRequest
    {
        [JsonProperty("tx_hash")] public string TxHash { get; set; }
    }

    public class GetTxDetailsResponse
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("tx_info")] public JObject TxInfo { get; set; }
    }
}
