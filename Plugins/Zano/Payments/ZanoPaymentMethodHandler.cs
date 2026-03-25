using System;
using System.Security.Cryptography;
using System.Threading.Tasks;

using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano.RPC.Models;
using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Plugins.Zano.Utils;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Zano.Payments
{
    public class ZanoPaymentMethodHandler : IPaymentMethodHandler
    {
        private readonly ZanoSpecificBtcPayNetwork _network;
        public ZanoSpecificBtcPayNetwork Network => _network;
        public JsonSerializer Serializer { get; }
        private readonly ZanoRpcProvider _zanoRpcProvider;

        public PaymentMethodId PaymentMethodId { get; }

        // Fixed fee: 0.01 ZANO in atomic units (12 decimals)
        private const long FixedFeeAtomicUnits = 10_000_000_000;

        public ZanoPaymentMethodHandler(ZanoSpecificBtcPayNetwork network, ZanoRpcProvider zanoRpcProvider)
        {
            PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
            _network = network;
            Serializer = BlobSerializer.CreateSerializer().Serializer;
            _zanoRpcProvider = zanoRpcProvider;
        }

        bool IsReady() => _zanoRpcProvider.IsConfigured(_network.CryptoCode) && _zanoRpcProvider.IsAvailable(_network.CryptoCode);

        public Task BeforeFetchingRates(PaymentMethodContext context)
        {
            context.Prompt.Currency = _network.CryptoCode;
            context.Prompt.Divisibility = _network.Divisibility;
            if (context.Prompt.Activated && IsReady())
            {
                try
                {
                    var paymentId = GeneratePaymentId();
                    var walletClient = _zanoRpcProvider.WalletRpcClients[_network.CryptoCode];
                    context.State = new Prepare()
                    {
                        ReserveAddress = walletClient.SendCommandAsync<MakeIntegratedAddressRequest, MakeIntegratedAddressResponse>(
                            "make_integrated_address",
                            new MakeIntegratedAddressRequest() { PaymentId = paymentId }),
                        PaymentId = paymentId
                    };
                }
                catch (Exception ex)
                {
                    context.Logs.Write($"Error in BeforeFetchingRates: {ex.Message}", InvoiceEventData.EventSeverity.Error);
                }
            }
            return Task.CompletedTask;
        }

        public async Task ConfigurePrompt(PaymentMethodContext context)
        {
            if (!_zanoRpcProvider.IsConfigured(_network.CryptoCode))
            {
                throw new PaymentMethodUnavailableException("BTCPAY_ZANO_WALLET_DAEMON_URI or BTCPAY_ZANO_DAEMON_URI isn't configured");
            }

            if (!_zanoRpcProvider.IsAvailable(_network.CryptoCode) || context.State is not Prepare zanoPrepare)
            {
                throw new PaymentMethodUnavailableException("Node or wallet not available");
            }

            var address = await zanoPrepare.ReserveAddress;

            var details = new ZanoOnChainPaymentMethodDetails()
            {
                PaymentId = zanoPrepare.PaymentId,
                InvoiceSettledConfirmationThreshold = ParsePaymentMethodConfig(context.PaymentMethodConfig).InvoiceSettledConfirmationThreshold
            };
            context.Prompt.Destination = address.IntegratedAddress;
            context.Prompt.PaymentMethodFee = ZanoMoney.Convert(FixedFeeAtomicUnits);
            context.Prompt.Details = JObject.FromObject(details, Serializer);
            context.TrackedDestinations.Add(address.IntegratedAddress);
        }

        private ZanoPaymentPromptDetails ParsePaymentMethodConfig(JToken config)
        {
            return config.ToObject<ZanoPaymentPromptDetails>(Serializer) ?? throw new FormatException($"Invalid {nameof(ZanoPaymentMethodHandler)}");
        }
        object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
        {
            return ParsePaymentMethodConfig(config);
        }

        class Prepare
        {
            public Task<MakeIntegratedAddressResponse> ReserveAddress;
            public string PaymentId;
        }

        public ZanoOnChainPaymentMethodDetails ParsePaymentPromptDetails(JToken details)
        {
            return details.ToObject<ZanoOnChainPaymentMethodDetails>(Serializer);
        }
        object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
        {
            return ParsePaymentPromptDetails(details);
        }

        public ZanoPaymentData ParsePaymentDetails(JToken details)
        {
            return details.ToObject<ZanoPaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(ZanoPaymentMethodHandler)}");
        }
        object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
        {
            return ParsePaymentDetails(details);
        }

        private static string GeneratePaymentId()
        {
            var bytes = new byte[8];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
