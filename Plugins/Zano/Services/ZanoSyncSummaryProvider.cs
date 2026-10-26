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
            // No configured Zano networks impose no sync requirement on the host.
            // Configured networks must use the same readiness check as payment handling.
            return _zanoRpcProvider.DaemonRpcClients.Keys.All(_zanoRpcProvider.IsAvailable);
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
                return ZanoRpcProvider.IsAvailable(Summary);
            }
        }

        public ZanoRpcProvider.ZanoSummary Summary { get; set; }
    }
}