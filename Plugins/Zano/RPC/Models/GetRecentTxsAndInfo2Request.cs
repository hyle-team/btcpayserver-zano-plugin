using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetRecentTxsAndInfo2Request
    {
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("update_provision_info")] public bool UpdateProvisionInfo { get; set; }
    }
}
