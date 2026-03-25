using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetWalletInfoResponse
    {
        [JsonProperty("address")] public string Address { get; set; }
        [JsonProperty("current_height")] public long CurrentHeight { get; set; }
        [JsonProperty("is_whatch_only")] public bool IsWatchOnly { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
    }
}
