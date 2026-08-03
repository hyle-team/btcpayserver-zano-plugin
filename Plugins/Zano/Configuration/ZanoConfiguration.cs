using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Zano.Configuration
{
    public class ZanoConfiguration
    {
        public Dictionary<string, ZanoConfigurationItem> ZanoConfigurationItems { get; set; } = [];

        // Groups configured networks by the canonical form of their wallet RPC URI.
        // CAs registered via BTCPAY_ZANO_EXTRA_ASSETS share the wallet of native ZANO,
        // and BTCPay's hosted service can use this to run one polling loop per wallet
        // instead of one per crypto code — the actual wallet history fetch is the same
        // regardless of which crypto we asked about, so N CAs sharing a wallet should
        // not cost the wallet daemon N+1× the necessary load.
        public IReadOnlyList<ZanoWalletGroup> GroupByWallet() =>
            ZanoConfigurationItems
                .GroupBy(kv => CanonicalWalletKey(kv.Value.InternalWalletRpcUri), StringComparer.Ordinal)
                .Select(g => new ZanoWalletGroup(g.Key, g.Select(kv => kv.Key).ToList()))
                .ToList();

        public static string CanonicalWalletKey(Uri walletUri) =>
            walletUri is null ? string.Empty : walletUri.AbsoluteUri.ToLowerInvariant();
    }

    public class ZanoConfigurationItem
    {
        public Uri DaemonRpcUri { get; set; }
        public Uri InternalWalletRpcUri { get; set; }
        public string WalletDirectory { get; set; }
    }

    public record ZanoWalletGroup(string WalletKey, IReadOnlyList<string> CryptoCodes);
}
