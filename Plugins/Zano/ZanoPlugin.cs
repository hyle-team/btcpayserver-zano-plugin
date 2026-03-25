using System;
using System.Globalization;
using System.Linq;

using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.Payments;
using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NBitcoin;

using NBXplorer;

namespace BTCPayServer.Plugins.Zano;

public class ZanoPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;
        var prov = pluginServices.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>();
        var chainName = prov.NetworkType;

        var network = new ZanoSpecificBtcPayNetwork()
        {
            CryptoCode = "ZANO",
            DisplayName = "Zano",
            Divisibility = 12,
            DefaultRateRules = new[]
            {
                    "ZANO_X = ZANO_BTC * BTC_X",
                    "ZANO_BTC = coingecko(ZANO_BTC)"
                },
            CryptoImagePath = "zano.svg",
            UriScheme = "zano"
        };
        var blockExplorerLink = chainName == ChainName.Mainnet
                    ? "https://explorer.zano.org/transaction/{0}"
                    : "https://testnet-explorer.zano.org/transaction/{0}";
        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId("ZANO");
        services.AddDefaultPrettyName(pmi, network.DisplayName);
        services.AddBTCPayNetwork(network)
                .AddTransactionLinkProvider(pmi, new SimpleTransactionLinkProvider(blockExplorerLink));

        services.AddSingleton(provider =>
                ConfigureZanoConfiguration(provider));
        services.AddHttpClient("ZanoClient");
        services.AddSingleton<ZanoRpcProvider>();
        services.AddHostedService<ZanoSummaryUpdaterHostedService>();
        services.AddHostedService<ZanoListener>();
        services.AddHostedService<ZanoLoadUpService>();
        services.AddSingleton(provider =>
                (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(ZanoPaymentMethodHandler), network));
        services.AddSingleton(provider =>
(IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZanoPaymentLinkExtension), network, pmi));
        services.AddSingleton(provider =>
(ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZanoCheckoutModelExtension), network, pmi));

        services.AddUIExtension("store-wallets-nav", "/Views/Zano/StoreWalletsNavZanoExtension.cshtml");
        services.AddUIExtension("store-invoices-payments", "/Views/Zano/ViewZanoPaymentData.cshtml");
        services.AddSingleton<ISyncSummaryProvider, ZanoSyncSummaryProvider>();
    }
    class SimpleTransactionLinkProvider : DefaultTransactionLinkProvider
    {
        public SimpleTransactionLinkProvider(string blockExplorerLink) : base(blockExplorerLink)
        {
        }

        public override string GetTransactionLink(string paymentId)
        {
            if (string.IsNullOrEmpty(BlockExplorerLink))
            {
                return null;
            }
            return string.Format(CultureInfo.InvariantCulture, BlockExplorerLink, paymentId);
        }
    }

    private static ZanoConfiguration ConfigureZanoConfiguration(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetService<IConfiguration>();
        var btcPayNetworkProvider = serviceProvider.GetService<BTCPayNetworkProvider>();
        var result = new ZanoConfiguration();

        var supportedNetworks = btcPayNetworkProvider.GetAll()
            .OfType<ZanoSpecificBtcPayNetwork>();

        foreach (var zanoNetwork in supportedNetworks)
        {
            var daemonUri =
                configuration.GetOrDefault<Uri>($"{zanoNetwork.CryptoCode}_daemon_uri",
                    null);
            var walletDaemonUri =
                configuration.GetOrDefault<Uri>(
                    $"{zanoNetwork.CryptoCode}_wallet_daemon_uri", null);
            var walletDaemonWalletDirectory =
                configuration.GetOrDefault<string>(
                    $"{zanoNetwork.CryptoCode}_wallet_daemon_walletdir", null);
            if (daemonUri == null || walletDaemonUri == null)
            {
                var logger = serviceProvider.GetRequiredService<ILogger<ZanoPlugin>>();
                var cryptoCode = zanoNetwork.CryptoCode.ToUpperInvariant();
                if (daemonUri is null)
                {
                    logger.LogWarning("BTCPAY_{CryptoCode}_DAEMON_URI is not configured", cryptoCode);
                }
                if (walletDaemonUri is null)
                {
                    logger.LogWarning("BTCPAY_{CryptoCode}_WALLET_DAEMON_URI is not configured", cryptoCode);
                }
                logger.LogWarning("{CryptoCode} got disabled as it is not fully configured.", cryptoCode);
            }
            else
            {
                result.ZanoConfigurationItems.Add(zanoNetwork.CryptoCode, new ZanoConfigurationItem
                {
                    DaemonRpcUri = daemonUri,
                    InternalWalletRpcUri = walletDaemonUri,
                    WalletDirectory = walletDaemonWalletDirectory
                });
            }
        }
        return result;
    }
}
