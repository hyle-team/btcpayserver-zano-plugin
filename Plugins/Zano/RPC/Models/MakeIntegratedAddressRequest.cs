using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class MakeIntegratedAddressRequest
    {
        [JsonProperty("payment_id")] public string PaymentId { get; set; }
    }
}
