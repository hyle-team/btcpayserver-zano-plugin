using System.Collections.Generic;

namespace BTCPayServer.Plugins.Zano.RPC
{
    // Emitted once per wallet-URI group per poll tick. The listener fetches the
    // wallet's recent transfers a single time and fans the result out across every
    // crypto code in the group, so N CAs sharing one wallet cost one
    // get_recent_txs_and_info2 instead of N+1.
    public class ZanoWalletPollEvent
    {
        public string WalletKey { get; set; }
        public IReadOnlyList<string> CryptoCodes { get; set; }
    }
}