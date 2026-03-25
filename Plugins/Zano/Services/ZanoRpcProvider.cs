using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Http;
using System.Threading.Tasks;

using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.RPC;
using BTCPayServer.Plugins.Zano.RPC.Models;

namespace BTCPayServer.Plugins.Zano.Services
{
    public class ZanoRpcProvider
    {
        private readonly ZanoConfiguration _zanoConfiguration;
        private readonly EventAggregator _eventAggregator;
        public ImmutableDictionary<string, JsonRpcClient> DaemonRpcClients;
        public ImmutableDictionary<string, JsonRpcClient> WalletRpcClients;

        public ConcurrentDictionary<string, ZanoSummary> Summaries { get; } = new();

        public ZanoRpcProvider(ZanoConfiguration zanoConfiguration,
            EventAggregator eventAggregator,
            IHttpClientFactory httpClientFactory)
        {
            _zanoConfiguration = zanoConfiguration;
            _eventAggregator = eventAggregator;
            DaemonRpcClients =
                _zanoConfiguration.ZanoConfigurationItems.ToImmutableDictionary(pair => pair.Key,
                    pair => new JsonRpcClient(pair.Value.DaemonRpcUri,
                        httpClientFactory.CreateClient($"{pair.Key}client")));
            WalletRpcClients =
                _zanoConfiguration.ZanoConfigurationItems.ToImmutableDictionary(pair => pair.Key,
                    pair => new JsonRpcClient(pair.Value.InternalWalletRpcUri,
                        httpClientFactory.CreateClient($"{pair.Key}client")));
        }

        public bool IsConfigured(string cryptoCode) => WalletRpcClients.ContainsKey(cryptoCode) && DaemonRpcClients.ContainsKey(cryptoCode);
        public bool IsAvailable(string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            return Summaries.ContainsKey(cryptoCode) && IsAvailable(Summaries[cryptoCode]);
        }

        private bool IsAvailable(ZanoSummary summary)
        {
            return summary.Synced &&
                   summary.WalletAvailable;
        }

        public async Task<ZanoSummary> UpdateSummary(string cryptoCode)
        {
            if (!DaemonRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var daemonRpcClient) ||
                !WalletRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var walletRpcClient))
            {
                return null;
            }

            var summary = new ZanoSummary();
            try
            {
                var daemonResult =
                    await daemonRpcClient.SendCommandAsync<GetInfoRequest, GetInfoResponse>("getinfo",
                        new GetInfoRequest());
                summary.CurrentHeight = daemonResult.Height;
                // daemon_network_state: 2 = online/synced
                summary.Synced = daemonResult.DaemonNetworkState == 2;
                summary.TargetHeight = daemonResult.MaxNetSeenHeight > 0
                    ? daemonResult.MaxNetSeenHeight
                    : summary.CurrentHeight;
                summary.UpdatedAt = DateTime.UtcNow;
                summary.DaemonAvailable = true;
            }
            catch
            {
                summary.DaemonAvailable = false;
            }
            try
            {
                var walletResult =
                    await walletRpcClient.SendCommandAsync<JsonRpcClient.NoRequestModel, GetWalletInfoResponse>(
                        "get_wallet_info", JsonRpcClient.NoRequestModel.Instance);
                summary.WalletHeight = walletResult.CurrentHeight;
                summary.WalletAvailable = true;
            }
            catch
            {
                summary.WalletAvailable = false;
            }

            var changed = !Summaries.ContainsKey(cryptoCode) || IsAvailable(cryptoCode) != IsAvailable(summary);

            Summaries[cryptoCode] = summary;
            if (changed)
            {
                _eventAggregator.Publish(new ZanoDaemonStateChange() { Summary = summary, CryptoCode = cryptoCode });
            }

            return summary;
        }

        public class ZanoDaemonStateChange
        {
            public string CryptoCode { get; set; }
            public ZanoSummary Summary { get; set; }
        }

        public class ZanoSummary
        {
            public bool Synced { get; set; }
            public long CurrentHeight { get; set; }
            public long WalletHeight { get; set; }
            public long TargetHeight { get; set; }
            public DateTime UpdatedAt { get; set; }
            public bool DaemonAvailable { get; set; }
            public bool WalletAvailable { get; set; }
        }
    }
}
