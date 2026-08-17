using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Zano.RPC.Models
{
    public class GetTxDetailsRequest
    {
        [JsonProperty("tx_hash")] public string TxHash { get; set; }
    }

    public class GetTxDetailsResponse
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("tx_info")] public GetTxDetailsTxInfo TxInfo { get; set; }
    }

    // Subset of Zano's tx_rpc_extended_info. keeper_block is the height of the block
    // that contains the transaction, or -1 while it is only in the tx pool
    // (core_rpc_server_commands_defs.h). It is the only field reconciliation needs:
    // it tells chain placement apart from pool presence and lets a recovered payment's
    // confirmations be recomputed instead of trusted from a stale record.
    public class GetTxDetailsTxInfo
    {
        [JsonProperty("keeper_block")] public long? KeeperBlock { get; set; }
        [JsonProperty("timestamp")] public long? Timestamp { get; set; }
    }
}
