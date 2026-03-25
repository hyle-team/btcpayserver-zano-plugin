using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Zano.Configuration
{
    public class ZanoConfiguration
    {
        public Dictionary<string, ZanoConfigurationItem> ZanoConfigurationItems { get; set; } = [];
    }

    public class ZanoConfigurationItem
    {
        public Uri DaemonRpcUri { get; set; }
        public Uri InternalWalletRpcUri { get; set; }
        public string WalletDirectory { get; set; }
    }
}
