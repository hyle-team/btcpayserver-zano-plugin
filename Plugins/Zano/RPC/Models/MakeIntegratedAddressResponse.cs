using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class MakeIntegratedAddressResponse
    {
        [JsonProperty("integrated_address")] public string IntegratedAddress { get; set; }
        [JsonProperty("payment_id")] public string PaymentId { get; set; }
    }
}
