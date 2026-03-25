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
            return _zanoRpcProvider.Summaries.All(pair => pair.Value.DaemonAvailable);
        }

        public string Partial { get; } = "/Views/Zano/ZanoSyncSummary.cshtml";
        public IEnumerable<ISyncStatus> GetStatuses()
        {
            return _zanoRpcProvider.Summaries.Select(pair => new ZanoSyncStatus()
            {
                Summary = pair.Value,
                PaymentMethodId = PaymentMethodId.Parse(pair.Key).ToString()
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