using System;
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
            foreach (var configItem in _zanoConfiguration.ZanoConfigurationItems)
            {
                _ = StartSummaryLoop(_cts.Token, configItem.Key);
                _ = StartPollingLoop(_cts.Token, configItem.Key);
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

        private async Task StartPollingLoop(CancellationToken cancellation, string cryptoCode)
        {
            Logs.PayServer.LogInformation("Starting Zano payment polling loop ({CryptoCode})", cryptoCode);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        if (_zanoRpcProvider.IsAvailable(cryptoCode))
                        {
                            _eventAggregator.Publish(new ZanoPollEvent { CryptoCode = cryptoCode });
                        }
                        await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
                    }
                    catch (Exception ex) when (!cancellation.IsCancellationRequested)
                    {
                        Logs.PayServer.LogError(ex, "Unhandled exception in polling loop ({CryptoCode})", cryptoCode);
                        await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
                    }
                }
            }
            catch when (cancellation.IsCancellationRequested)
            {
                // ignored
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            return Task.CompletedTask;
        }
    }
}
