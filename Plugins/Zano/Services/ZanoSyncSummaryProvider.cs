using System.Collections.Generic;
using System.Linq;

using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Zano.Services
{
    public class ZanoSyncSummaryProvider : ISyncSummaryProvider
    {
        private readonly ZanoRpcProvider _zanoRpcProvider;

        public ZanoSyncSummaryProvider(ZanoRpcProvider zanoRpcProvider)
        {
            _zanoRpcProvider = zanoRpcProvider;
        }

        public bool AllAvailable()
        {
            // Match ZanoRpcProvider.IsAvailable: a network is ready only when its summary
            // exists, the daemon reports synced (DaemonNetworkState == 2 sets Synced=true),
            // and the wallet RPC is reachable. Empty Summaries means nothing has been
            // polled yet — report unavailable so /api/v1/server/info doesn't claim
            // fullySynched=true during plugin startup or when no Zano network is configured.
            var configured = _zanoRpcProvider.DaemonRpcClients;
            if (configured.Count == 0)
            {
                return false;
            }
            return configured.Keys.All(code =>
                _zanoRpcProvider.Summaries.TryGetValue(code, out var s)
                && s.Synced
                && s.WalletAvailable);
        }

        public string Partial { get; } = "/Views/Zano/ZanoSyncSummary.cshtml";
        public IEnumerable<ISyncStatus> GetStatuses()
        {
            return _zanoRpcProvider.Summaries.Select(pair => new ZanoSyncStatus()
            {
                Summary = pair.Value,
                // Emit the canonical "ZANO-CHAIN" / "ZANO<ASSET>-CHAIN" form so API clients
                // can correlate sync entries with supportedPaymentMethods. PaymentMethodId
                // .Parse on a bare crypto code does not produce that form.
                PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(pair.Key).ToString()
            });
        }
    }

    public class ZanoSyncStatus : SyncStatus, ISyncStatus
    {
        public override bool Available
        {
            get
            {
                return Summary?.WalletAvailable ?? false;
            }
        }

        public ZanoRpcProvider.ZanoSummary Summary { get; set; }
    }
}