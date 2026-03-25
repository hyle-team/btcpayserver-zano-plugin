using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetInfoResponse
    {
        [JsonProperty("height")] public long Height { get; set; }
        [JsonProperty("daemon_network_state")] public int DaemonNetworkState { get; set; }
        [JsonProperty("synchronization_start_height")] public long SynchronizationStartHeight { get; set; }
        [JsonProperty("max_net_seen_height")] public long MaxNetSeenHeight { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
    }

    public class GetInfoRequest
    {
        [JsonProperty("flags")] public int Flags { get; set; } = 0x1 | 0x2 | 0x4 | 0x10 | 0x40;
    }
}
