using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class OpenWalletRequest
    {
        [JsonProperty("filename")] public string Filename { get; set; }
        [JsonProperty("password")] public string Password { get; set; }
    }
}