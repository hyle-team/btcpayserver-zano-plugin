using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.RPC.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Zano.Services;

public class ZanoLoadUpService : IHostedService
{
    private const string CryptoCode = "ZANO";
    private readonly ILogger<ZanoLoadUpService> _logger;
    private readonly ZanoRpcProvider _zanoRpcProvider;
    private readonly ZanoConfiguration _zanoConfiguration;

    public ZanoLoadUpService(ILogger<ZanoLoadUpService> logger, ZanoRpcProvider zanoRpcProvider, ZanoConfiguration zanoConfiguration)
    {
        _zanoRpcProvider = zanoRpcProvider;
        _zanoConfiguration = zanoConfiguration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_zanoConfiguration.ZanoConfigurationItems.TryGetValue(CryptoCode, out var configItem))
            {
                _logger.LogInformation("No Zano configuration found, skipping wallet load");
                return;
            }

            var walletDir = configItem.WalletDirectory;
            if (!string.IsNullOrEmpty(walletDir))
            {
                _logger.LogInformation("Attempting to load existing Zano wallet");

                string password = await TryToGetPassword(walletDir, cancellationToken);

                await _zanoRpcProvider.WalletRpcClients[CryptoCode]
                    .SendCommandAsync<OpenWalletRequest, OpenWalletResponse>("open_wallet",
                        new OpenWalletRequest { Filename = "wallet", Password = password }, cancellationToken);

                await _zanoRpcProvider.UpdateSummary(CryptoCode);
                _logger.LogInformation("Existing Zano wallet successfully loaded");
            }
            else
            {
                _logger.LogInformation("No wallet directory configured. Wallet should be pre-opened in simplewallet RPC mode.");
                // Still try to update summary — wallet may already be running in RPC mode
                await _zanoRpcProvider.UpdateSummary(CryptoCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to load {CryptoCode} wallet. Error: {ErrorMessage}", CryptoCode, ex.Message);
        }
    }

    private async Task<string> TryToGetPassword(string walletDir, CancellationToken cancellationToken)
    {
        string password = "";
        string passwordFile = Path.Combine(walletDir, "password");
        if (File.Exists(passwordFile))
        {
            password = await File.ReadAllTextAsync(passwordFile, cancellationToken);
            password = password.Trim();
        }
        else
        {
            _logger.LogInformation("No password file found - using empty password");
        }

        return password;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
