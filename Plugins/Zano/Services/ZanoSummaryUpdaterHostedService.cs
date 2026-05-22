using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using BTCPayServer.Logging;
using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.RPC;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Zano.Services
{
    public class ZanoSummaryUpdaterHostedService : IHostedService
    {
        private readonly ZanoRpcProvider _zanoRpcProvider;
        private readonly ZanoConfiguration _zanoConfiguration;
        private readonly EventAggregator _eventAggregator;

        public Logs Logs { get; }

        private CancellationTokenSource _cts;

        public ZanoSummaryUpdaterHostedService(ZanoRpcProvider zanoRpcProvider,
            ZanoConfiguration zanoConfiguration,
            EventAggregator eventAggregator,
            Logs logs)
        {
            _zanoRpcProvider = zanoRpcProvider;
            _zanoConfiguration = zanoConfiguration;
            _eventAggregator = eventAggregator;
            Logs = logs;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Summary updates stay per crypto code because each network can in principle
            // point at a different daemon URI (even though every CA today shares native
            // ZANO's wallet, the daemon URI is configured separately).
            foreach (var configItem in _zanoConfiguration.ZanoConfigurationItems)
            {
                _ = StartSummaryLoop(_cts.Token, configItem.Key);
            }
            // Polling is consolidated per wallet URI: one ZanoWalletPollEvent per wallet
            // per tick, which the listener handles with a single get_recent_txs_and_info2
            // fanned out across every network in the group.
            foreach (var walletGroup in _zanoConfiguration.GroupByWallet())
            {
                _ = StartWalletPollLoop(_cts.Token, walletGroup);
            }
            return Task.CompletedTask;
        }

        private async Task StartSummaryLoop(CancellationToken cancellation, string cryptoCode)
        {
            Logs.PayServer.LogInformation("Starting Zano daemon summary updater ({CryptoCode})", cryptoCode);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await _zanoRpcProvider.UpdateSummary(cryptoCode);
                        if (_zanoRpcProvider.IsAvailable(cryptoCode))
                        {
                            await Task.Delay(TimeSpan.FromMinutes(1), cancellation);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
                        }
                    }
                    catch (Exception ex) when (!cancellation.IsCancellationRequested)
                    {
                        Logs.PayServer.LogError(ex, "Unhandled exception in summary updater ({CryptoCode})", cryptoCode);
                        await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
                    }
                }
            }
            catch when (cancellation.IsCancellationRequested)
            {
                // ignored
            }
        }

        private async Task StartWalletPollLoop(CancellationToken cancellation, ZanoWalletGroup walletGroup)
        {
            Logs.PayServer.LogInformation(
                "Starting Zano payment polling loop (wallet {WalletKey}, networks {CryptoCodes})",
                walletGroup.WalletKey, string.Join(",", walletGroup.CryptoCodes));
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        if (AnyAvailable(walletGroup.CryptoCodes))
                        {
                            _eventAggregator.Publish(new ZanoWalletPollEvent
                            {
                                WalletKey = walletGroup.WalletKey,
                                CryptoCodes = walletGroup.CryptoCodes
                            });
                        }
                        await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
                    }
                    catch (Exception ex) when (!cancellation.IsCancellationRequested)
                    {
                        Logs.PayServer.LogError(ex,
                            "Unhandled exception in polling loop (wallet {WalletKey})", walletGroup.WalletKey);
                        await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
                    }
                }
            }
            catch when (cancellation.IsCancellationRequested)
            {
                // ignored
            }
        }

        private bool AnyAvailable(IReadOnlyList<string> cryptoCodes)
        {
            foreach (var code in cryptoCodes)
            {
                if (_zanoRpcProvider.IsAvailable(code))
                {
                    return true;
                }
            }
            return false;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            return Task.CompletedTask;
        }
    }
}
