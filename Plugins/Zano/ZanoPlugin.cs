using System;
using System.Collections.Generic;
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
    private const string NativeCryptoCode = "ZANO";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;
        var prov = pluginServices.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>();
        var chainName = prov.NetworkType;
        var configuration = pluginServices.BootstrapServices.GetRequiredService<IConfiguration>();

        var blockExplorerLink = chainName == ChainName.Mainnet
                    ? "https://explorer.zano.org/transaction/{0}"
                    : "https://testnet-explorer.zano.org/transaction/{0}";

        var nativeNetwork = new ZanoSpecificBtcPayNetwork()
        {
            CryptoCode = NativeCryptoCode,
            DisplayName = "Zano",
            Divisibility = ZanoAssets.NativeDecimals,
            DefaultRateRules = new[]
            {
                    "ZANO_X = ZANO_BTC * BTC_X",
                    "ZANO_BTC = coingecko(ZANO_BTC)"
                },
            CryptoImagePath = "zano.svg",
            UriScheme = "zano",
            AssetId = ZanoAssets.NativeAssetId,
            AssetTicker = ZanoAssets.NativeTicker,
            IsNative = true
        };

        RegisterNetwork(services, nativeNetwork, blockExplorerLink);

        var extraAssets = ParseExtraAssets(configuration, pluginServices);
        foreach (var asset in extraAssets)
        {
            var caNetwork = new ZanoSpecificBtcPayNetwork()
            {
                CryptoCode = asset.CryptoCode,
                DisplayName = asset.DisplayName,
                Divisibility = asset.Decimals,
                DefaultRateRules = asset.BuildDefaultRateRules(),
                CryptoImagePath = "zano.svg",
                UriScheme = "zano",
                AssetId = asset.AssetId,
                AssetTicker = asset.Ticker,
                IsNative = false
            };
            RegisterNetwork(services, caNetwork, blockExplorerLink);
        }

        services.AddSingleton(provider =>
                ConfigureZanoConfiguration(provider));
        services.AddHttpClient("ZanoClient");
        services.AddSingleton<ZanoRpcProvider>();
        services.AddHostedService<ZanoSummaryUpdaterHostedService>();
        services.AddHostedService<ZanoListener>();
        services.AddHostedService<ZanoLoadUpService>();

        services.AddUIExtension("store-wallets-nav", "/Views/Zano/StoreWalletsNavZanoExtension.cshtml");
        services.AddUIExtension("store-invoices-payments", "/Views/Zano/ViewZanoPaymentData.cshtml");
        services.AddSingleton<ISyncSummaryProvider, ZanoSyncSummaryProvider>();
    }

    private static void RegisterNetwork(IServiceCollection services, ZanoSpecificBtcPayNetwork network, string blockExplorerLink)
    {
        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
        services.AddDefaultPrettyName(pmi, network.DisplayName);
        services.AddBTCPayNetwork(network)
                .AddTransactionLinkProvider(pmi, new SimpleTransactionLinkProvider(blockExplorerLink));
        services.AddSingleton(provider =>
                (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(ZanoPaymentMethodHandler), network));
        services.AddSingleton(provider =>
                (IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZanoPaymentLinkExtension), network, pmi));
        services.AddSingleton(provider =>
                (ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZanoCheckoutModelExtension), network, pmi));
    }

    private static IReadOnlyList<ZanoAssetDefinition> ParseExtraAssets(IConfiguration configuration, PluginServiceCollection pluginServices)
    {
        var raw = configuration.GetOrDefault<string>("ZANO_EXTRA_ASSETS", null);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<ZanoAssetDefinition>();
        }
        try
        {
            return ZanoAssetDefinition.ParseExtraAssets(raw);
        }
        catch (FormatException ex)
        {
            var loggerFactory = pluginServices.BootstrapServices.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<ZanoPlugin>();
            logger?.LogError(ex, "BTCPAY_ZANO_EXTRA_ASSETS is malformed; extra assets disabled: {Message}", ex.Message);
            return Array.Empty<ZanoAssetDefinition>();
        }
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
        var logger = serviceProvider.GetRequiredService<ILogger<ZanoPlugin>>();
        var result = new ZanoConfiguration();

        var supportedNetworks = btcPayNetworkProvider.GetAll()
            .OfType<ZanoSpecificBtcPayNetwork>()
            .ToList();

        // Extra-asset networks (IsNative=false) share the daemon/wallet of native ZANO.
        // We resolve the shared URIs from the native entry's env vars and reuse them.
        var daemonUri = configuration.GetOrDefault<Uri>($"{NativeCryptoCode}_daemon_uri", null);
        var walletDaemonUri = configuration.GetOrDefault<Uri>($"{NativeCryptoCode}_wallet_daemon_uri", null);
        var walletDaemonWalletDirectory = configuration.GetOrDefault<string>($"{NativeCryptoCode}_wallet_daemon_walletdir", null);

        if (daemonUri == null || walletDaemonUri == null)
        {
            if (daemonUri is null)
            {
                logger.LogWarning("BTCPAY_{CryptoCode}_DAEMON_URI is not configured", NativeCryptoCode);
            }
            if (walletDaemonUri is null)
            {
                logger.LogWarning("BTCPAY_{CryptoCode}_WALLET_DAEMON_URI is not configured", NativeCryptoCode);
            }
            logger.LogWarning("Zano (and any configured extra assets) got disabled as it is not fully configured.");
            return result;
        }

        foreach (var zanoNetwork in supportedNetworks)
        {
            result.ZanoConfigurationItems.Add(zanoNetwork.CryptoCode, new ZanoConfigurationItem
            {
                DaemonRpcUri = daemonUri,
                InternalWalletRpcUri = walletDaemonUri,
                WalletDirectory = walletDaemonWalletDirectory
            });
        }
        return result;
    }
}
