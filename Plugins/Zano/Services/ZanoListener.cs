using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.Payments;
using BTCPayServer.Plugins.Zano.RPC;
using BTCPayServer.Plugins.Zano.RPC.Models;
using BTCPayServer.Plugins.Zano.Utils;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;

using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Zano.Services
{
    public class ZanoListener : EventHostedServiceBase
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly EventAggregator _eventAggregator;
        private readonly ZanoRpcProvider _zanoRpcProvider;
        private readonly ZanoConfiguration _zanoConfiguration;
        private readonly BTCPayNetworkProvider _networkProvider;
        private readonly ILogger<ZanoListener> _logger;
        private readonly PaymentMethodHandlerDictionary _handlers;
        private readonly InvoiceActivator _invoiceActivator;
        private readonly PaymentService _paymentService;

        // One SemaphoreSlim(1,1) per wallet URI group, lazy-initialized on first use.
        // Serializes UpdateAllPaymentStatesForWalletGroupAsync so the regular poll
        // (ZanoWalletPollEvent) and availability-triggered scans (ZanoDaemonStateChange)
        // never overlap against the same wallet — duplicate scans cost RPC, race on the
        // shared updatedPaymentEntities list, and the old fire-and-forget on state
        // change dropped exceptions outside the inner RPC catch.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _walletScanSemaphores = new(StringComparer.Ordinal);

        // Resolved once from ZanoConfiguration.GroupByWallet(). Maps a single crypto
        // code to the wallet group it belongs to, so ZanoDaemonStateChange (per crypto)
        // can dispatch into the consolidated wallet-level scan.
        private readonly IReadOnlyDictionary<string, ZanoWalletGroup> _walletGroupByCryptoCode;

        public ZanoListener(InvoiceRepository invoiceRepository,
            EventAggregator eventAggregator,
            ZanoRpcProvider zanoRpcProvider,
            ZanoConfiguration zanoConfiguration,
            BTCPayNetworkProvider networkProvider,
            ILogger<ZanoListener> logger,
            PaymentMethodHandlerDictionary handlers,
            InvoiceActivator invoiceActivator,
            PaymentService paymentService) : base(eventAggregator, logger)
        {
            _invoiceRepository = invoiceRepository;
            _eventAggregator = eventAggregator;
            _zanoRpcProvider = zanoRpcProvider;
            _zanoConfiguration = zanoConfiguration;
            _networkProvider = networkProvider;
            _logger = logger;
            _handlers = handlers;
            _invoiceActivator = invoiceActivator;
            _paymentService = paymentService;

            var groups = zanoConfiguration.GroupByWallet();
            var byCrypto = new Dictionary<string, ZanoWalletGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                foreach (var code in g.CryptoCodes)
                {
                    byCrypto[code] = g;
                }
            }
            _walletGroupByCryptoCode = byCrypto;
        }

        protected override void SubscribeToEvents()
        {
            base.SubscribeToEvents();
            Subscribe<ZanoWalletPollEvent>();
            Subscribe<ZanoRpcProvider.ZanoDaemonStateChange>();
        }

        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            if (evt is ZanoRpcProvider.ZanoDaemonStateChange stateChange)
            {
                if (_zanoRpcProvider.IsAvailable(stateChange.CryptoCode))
                {
                    _logger.LogInformation("{CryptoCode} just became available", stateChange.CryptoCode);
                    if (_walletGroupByCryptoCode.TryGetValue(stateChange.CryptoCode, out var group))
                    {
                        // Awaited (was fire-and-forget): exceptions are observable
                        // via base ProcessEvent, and the per-wallet semaphore inside
                        // UpdateAllPaymentStatesForWalletGroupAsync prevents overlap
                        // with the regular ZanoWalletPollEvent scan.
                        await UpdateAllPaymentStatesForWalletGroupAsync(group, cancellationToken);
                    }
                }
                else
                {
                    _logger.LogInformation("{CryptoCode} just became unavailable", stateChange.CryptoCode);
                }
            }
            else if (evt is ZanoWalletPollEvent pollEvent)
            {
                var group = new ZanoWalletGroup(pollEvent.WalletKey, pollEvent.CryptoCodes);
                await UpdateAllPaymentStatesForWalletGroupAsync(group, cancellationToken);
            }
        }

        private async Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
        {
            _logger.LogInformation(
                "Invoice {InvoiceId} received payment {Value} {Currency} {PaymentId}",
                invoice.Id, payment.Value, payment.Currency, payment.Id);

            var prompt = invoice.GetPaymentPrompt(payment.PaymentMethodId);

            if (prompt != null &&
                prompt.Activated &&
                prompt.Destination == payment.Destination &&
                prompt.Calculate().Due > 0.0m)
            {
                await _invoiceActivator.ActivateInvoicePaymentMethod(invoice.Id, payment.PaymentMethodId, true);
                invoice = await _invoiceRepository.GetInvoice(invoice.Id);
            }

            _eventAggregator.Publish(
                new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }

        // Entry point used by both the per-wallet poll and the per-crypto state-change
        // handler. Acquires the per-wallet semaphore, gathers pending invoices across
        // every network in the group, issues ONE wallet-history fetch, and fans the
        // resulting transfers out to each network's candidate-processing pass.
        //
        // Before this consolidation, each registered network ran its own poll loop and
        // its own scan, so N CAs sharing a wallet meant N+1× the necessary load on
        // get_recent_txs_and_info2. Now the fetch happens once per wallet per tick.
        private async Task UpdateAllPaymentStatesForWalletGroupAsync(
            ZanoWalletGroup walletGroup,
            CancellationToken cancellationToken)
        {
            var sem = _walletScanSemaphores.GetOrAdd(walletGroup.WalletKey, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(cancellationToken);
            try
            {
                // Gather pending invoices per crypto. Skip cryptos that are unavailable
                // (their daemon hasn't reported synced) or that have no pending invoices.
                var perCrypto = new List<CryptoScanContext>();
                var allPaymentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                JsonRpcClient walletRpcClient = null;
                string walletClientCryptoCode = null;

                foreach (var cryptoCode in walletGroup.CryptoCodes)
                {
                    if (!_zanoRpcProvider.IsAvailable(cryptoCode))
                    {
                        continue;
                    }
                    var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
                    var invoices = await GetReconciliationInvoices(paymentMethodId, cancellationToken);
                    if (!invoices.Any())
                    {
                        continue;
                    }
                    invoices = invoices
                        .Where(entity => entity.GetPaymentPrompt(paymentMethodId)?.Activated is true)
                        .ToArray();
                    if (invoices.Length == 0)
                    {
                        continue;
                    }

                    var ctx = BuildExpandedInvoicesForCrypto(cryptoCode, invoices);
                    if (ctx.PaymentIds.Count == 0)
                    {
                        continue;
                    }
                    perCrypto.Add(ctx);
                    foreach (var pid in ctx.PaymentIds)
                    {
                        allPaymentIds.Add(pid);
                    }
                    walletRpcClient ??= _zanoRpcProvider.WalletRpcClients[cryptoCode];
                    walletClientCryptoCode ??= cryptoCode;
                }

                if (allPaymentIds.Count == 0 || walletRpcClient is null)
                {
                    return;
                }

                var (allTransfers, _) = await FetchWalletTransfersAsync(
                    walletRpcClient, walletClientCryptoCode, allPaymentIds, cancellationToken);

                foreach (var ctx in perCrypto)
                {
                    await ProcessTransfersForCryptoAsync(ctx, allTransfers, cancellationToken);
                }
            }
            finally
            {
                sem.Release();
            }
        }

        // Collect all payment_ids from pending invoices for a single crypto code.
        //
        // For each invoice we collect both the CURRENT prompt's payment_id AND the
        // payment_ids of every Zano payment already recorded against the invoice.
        // BTCPay re-activates the payment prompt (regenerating address + pid) after
        // a payment is registered while Due is still being recomputed against the
        // pre-AddPayment invoice snapshot. Without history, every subsequent poll
        // would key off the new prompt pid and never re-match the already-recorded
        // payment — its confirmations would stall at the value seen at first detection
        // and the invoice would never reach Settled.
        private CryptoScanContext BuildExpandedInvoicesForCrypto(string cryptoCode, InvoiceEntity[] invoices)
        {
            var network = (ZanoSpecificBtcPayNetwork)_networkProvider.GetNetwork(cryptoCode);
            var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
            var handler = (ZanoPaymentMethodHandler)_handlers[paymentMethodId];

            var expanded = invoices.Select(entity =>
                {
                    var existing = GetAllZanoPayments(entity, cryptoCode).ToList();
                    var existingPids = existing
                        .Select(p => handler.ParsePaymentDetails(p.Details)?.PaymentId)
                        .Where(pid => !string.IsNullOrEmpty(pid))
                        .ToList();
                    var promptDetails = handler.ParsePaymentPromptDetails(entity.GetPaymentPrompt(paymentMethodId).Details);
                    var allPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(promptDetails.PaymentId))
                    {
                        allPids.Add(promptDetails.PaymentId);
                    }
                    foreach (var pid in existingPids)
                    {
                        allPids.Add(pid);
                    }
                    return (
                        Invoice: entity,
                        ExistingPayments: (IEnumerable<PaymentEntity>)existing,
                        Prompt: entity.GetPaymentPrompt(paymentMethodId),
                        PaymentMethodDetails: promptDetails,
                        AllPaymentIds: allPids);
                })
                .ToList();

            var paymentIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in expanded)
            {
                foreach (var pid in e.AllPaymentIds)
                {
                    paymentIdSet.Add(pid);
                }
            }

            return new CryptoScanContext(cryptoCode, network, handler, expanded, paymentIdSet);
        }

        // get_bulk_payments is native-ZANO-only: it ignores asset_id and returns
        // amount=0 for CA transfers. get_recent_txs_and_info2 is the canonical
        // per-asset transfer list — its subtransfers_by_pid groups let us match
        // (payment_id, asset_id) and read the correct atomic amount for both
        // native and CA payments uniformly.
        //
        // We page until either every monitored payment_id has been matched, the
        // wallet history is exhausted (page returns < PageSize transfers), or a
        // safety cap is hit. Without paging, a busy wallet (one shared between
        // native ZANO and many CAs) could push a valid invoice payment past the
        // first page within minutes and the listener would never see it.
        //
        // Because the wallet's history is the same regardless of which crypto in
        // the group we asked through, a single call here serves every network in
        // the wallet group — the per-network filtering happens afterwards.
        // Returns the wallet transfer rows plus whether the scan was COMPLETE. Complete means
        // it ended by locating every monitored payment id or by exhausting the wallet history
        // — i.e. absence of a payment from the result is trustworthy. Incomplete means it hit
        // the page cap or an RPC error, so absence proves nothing and must NOT feed
        // drop-detection. Caller cancellation propagates rather than masquerading as an
        // empty/partial history.
        private async Task<(List<ZanoTransfer> Transfers, bool Complete)> FetchWalletTransfersAsync(
            JsonRpcClient walletRpcClient,
            string logCryptoCode,
            HashSet<string> paymentIdSet,
            CancellationToken cancellationToken)
        {
            const int PageSize = 200;
            const int MaxPages = 10;
            var allTransfers = new List<ZanoTransfer>();
            var matchedPaymentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int offset = 0;
            bool complete = false;
            for (int page = 0; page < MaxPages; page++)
            {
                GetRecentTxsAndInfo2Response result;
                try
                {
                    result = await walletRpcClient.SendCommandAsync<GetRecentTxsAndInfo2Request, GetRecentTxsAndInfo2Response>(
                        "get_recent_txs_and_info2",
                        new GetRecentTxsAndInfo2Request
                        {
                            Offset = offset,
                            Count = PageSize,
                            UpdateProvisionInfo = false
                        },
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown/cancellation is not "history exhausted" — propagate so the
                    // caller aborts instead of treating a partial prefix as authoritative.
                    throw;
                }
                catch (Exception ex)
                {
                    // RPC failure mid-paging: hand back what we have but mark INCOMPLETE so
                    // drop-detection does not read "absent" as "dropped".
                    _logger.LogError(ex,
                        "Failed to query recent txs via {CryptoCode} client at offset {Offset}",
                        logCryptoCode, offset);
                    return (DedupTransferRows(allTransfers), false);
                }

                if (result?.Transfers == null || result.Transfers.Count == 0)
                {
                    complete = true; // history exhausted
                    break;
                }

                allTransfers.AddRange(result.Transfers);

                foreach (var tx in result.Transfers)
                {
                    if (tx.SubtransfersByPid == null)
                    {
                        continue;
                    }
                    foreach (var group in tx.SubtransfersByPid)
                    {
                        if (!string.IsNullOrEmpty(group.PaymentId) && paymentIdSet.Contains(group.PaymentId))
                        {
                            matchedPaymentIds.Add(group.PaymentId);
                        }
                    }
                }

                if (matchedPaymentIds.Count >= paymentIdSet.Count)
                {
                    complete = true; // every monitored payment id located
                    break;
                }
                if (result.Transfers.Count < PageSize)
                {
                    complete = true; // history exhausted
                    break;
                }
                offset += PageSize;
                // Falling out of the loop via MaxPages leaves complete=false: the history is
                // deeper than our cap and some monitored payment id is still unseen.
            }

            return (DedupTransferRows(allTransfers), complete);
        }

        // Page-overlap guard: get_recent_txs_and_info2 pages newest-first by offset over a
        // growing history, so a tx near a page boundary can be returned on two consecutive
        // pages. AggregateCandidates SUMS same-(tx,pid,asset,height) legs — legitimate for a
        // multi-output payment within one tx — so a duplicated transfer ROW would double the
        // recorded amount. Collapse to one row per tx_hash, keeping the highest-height
        // instance (which also folds a mempool row into its later confirmed row).
        public static List<ZanoTransfer> DedupTransferRows(IEnumerable<ZanoTransfer> transfers)
            => transfers
                .GroupBy(t => t.TxHash ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(t => t.Height).First())
                .ToList();

        // Per-crypto candidate filtering and payment publication. Runs against the
        // wallet-level transfers already fetched by FetchWalletTransfersAsync; the
        // filtering keeps only income subtransfers whose payment_id is monitored AND
        // whose asset_id matches THIS network's asset, so multiple networks sharing
        // the same wallet (and the same transfer list) each see only their own
        // payments.
        private async Task ProcessTransfersForCryptoAsync(
            CryptoScanContext ctx,
            List<ZanoTransfer> allTransfers,
            CancellationToken cancellationToken)
        {
            long currentHeight = 0;
            if (_zanoRpcProvider.Summaries.TryGetValue(ctx.CryptoCode, out var summary))
            {
                currentHeight = summary.CurrentHeight;
            }

            // Flatten into (txHash, pid, assetId, amount, height, unlock) candidates,
            // filtered to income subtransfers whose payment_id matches a pending invoice
            // AND whose asset_id matches the current network's asset. This naturally
            // skips: our own outgoing tx legs, native-ZANO change outputs on CA txs,
            // and payments targeting other registered Zano networks.
            // Amount is decimal — see ZanoSubtransfer.Amount comment for the long-overflow
            // rationale on high-divisibility CAs.
            var candidates = new List<(decimal Amount, string PaymentId, string AssetId, string TxHash, long Height, long UnlockTime)>();
            foreach (var tx in allTransfers)
            {
                if (tx.SubtransfersByPid == null)
                {
                    continue;
                }
                foreach (var group in tx.SubtransfersByPid)
                {
                    if (string.IsNullOrEmpty(group.PaymentId) || !ctx.PaymentIds.Contains(group.PaymentId))
                    {
                        continue;
                    }
                    if (group.Subtransfers == null)
                    {
                        continue;
                    }
                    foreach (var sub in group.Subtransfers)
                    {
                        if (!sub.IsIncome)
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(ctx.Network.AssetId) &&
                            !string.Equals(sub.AssetId, ctx.Network.AssetId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        candidates.Add((sub.Amount, group.PaymentId, sub.AssetId, tx.TxHash, tx.Height, tx.UnlockTime));
                    }
                }
            }

            var updatedPaymentEntities = new List<(PaymentEntity Payment, InvoiceEntity invoice)>();
            var processingTasks = new List<Task>();

            var dedupedCandidates = AggregateCandidates(candidates);

            foreach (var cand in dedupedCandidates)
            {
                var matchingInvoice = ctx.ExpandedInvoices.FirstOrDefault(e =>
                    e.AllPaymentIds.Contains(cand.PaymentId) &&
                    (
                        string.IsNullOrEmpty(e.PaymentMethodDetails.AssetId) ||
                        string.IsNullOrEmpty(cand.AssetId) ||
                        string.Equals(cand.AssetId, e.PaymentMethodDetails.AssetId, StringComparison.OrdinalIgnoreCase)
                    ));
                if (matchingInvoice.Invoice == null)
                {
                    continue;
                }

                // Confirmations = chain size − inclusion height. getinfo.height is the
                // blockchain SIZE (top block index + 1) and cand.Height is the tx's 0-based
                // inclusion height, so a tx in the tip block has (size − height) = 1. The
                // old "+ 1" over-counted by one and settled ≥2-conf policies a block early.
                // Clamp ≥ 0: a brief reorg can drop currentHeight below cand.Height.
                long confirmations = cand.Height > 0 && currentHeight > 0
                    ? Math.Max(0L, currentHeight - cand.Height)
                    : 0;

                _logger.LogInformation(
                    "Zano CA candidate: cryptoCode={CryptoCode} div={Divisibility} pid={Pid} asset={AssetId} amount_atomic={Amount} txHash={Tx} h={Height}",
                    ctx.CryptoCode, ctx.Network.Divisibility, cand.PaymentId, cand.AssetId, cand.Amount, cand.TxHash, cand.Height);

                processingTasks.Add(HandlePaymentData(
                    ctx.CryptoCode,
                    cand.Amount,
                    cand.PaymentId,
                    cand.TxHash,
                    confirmations,
                    cand.Height,
                    cand.UnlockTime,
                    cand.AssetId,
                    matchingInvoice.Invoice,
                    updatedPaymentEntities));
            }

            await Task.WhenAll(processingTasks);

            // A history-window miss is never enough to invalidate a payment. Confirm the
            // exact transaction against the daemon before changing accounting state. This
            // remains safe when the wallet scan is capped, pruned, or simply too busy to
            // include an older payment. A daemon lookup that fails for any reason other
            // than "not found" is inconclusive and leaves the payment untouched.
            var matchedKeys = new HashSet<string>(
                dedupedCandidates.Select(c => DropDetectionKey(c.TxHash, c.PaymentId, c.AssetId)),
                StringComparer.OrdinalIgnoreCase);
            var invoicesToReopen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var invoiceCtx in ctx.ExpandedInvoices)
            {
                foreach (var existing in invoiceCtx.ExistingPayments)
                {
                    var existingDetails = ctx.Handler.ParsePaymentDetails(existing.Details);
                    if (existingDetails == null)
                    {
                        continue;
                    }
                    var key = DropDetectionKey(existingDetails.TransactionId, existingDetails.PaymentId, existingDetails.AssetId);
                    if (matchedKeys.Contains(key))
                    {
                        continue;
                    }

                    var txExists = await TransactionExistsAsync(
                        ctx.CryptoCode, existingDetails.TransactionId, cancellationToken);
                    if (txExists is not false)
                    {
                        if (txExists is true && existingDetails.MissingPollCount != 0)
                        {
                            existingDetails.MissingPollCount = 0;
                            existing.Details = JToken.FromObject(existingDetails, ctx.Handler.Serializer);
                            updatedPaymentEntities.Add((existing, invoiceCtx.Invoice));
                        }
                        continue;
                    }

                    existingDetails.MissingPollCount++;
                    if (ShouldUnaccountMissingPayment(existing.Status, existingDetails.MissingPollCount))
                    {
                        existing.Status = PaymentStatus.Unaccounted;
                        existingDetails.ConfirmationCount = 0;
                        if (ShouldReopenSettledInvoice(invoiceCtx.Invoice.Status, invoiceCtx.Invoice.ExceptionStatus))
                        {
                            invoicesToReopen.Add(invoiceCtx.Invoice.Id);
                        }
                        _logger.LogWarning(
                            "Zano reconciliation: transaction missing from daemon; invoice={InvoiceId} pid={Pid} tx={Tx} marked unaccounted after {Threshold} checks",
                            invoiceCtx.Invoice.Id, existingDetails.PaymentId, existingDetails.TransactionId, DropDetectionThreshold);
                    }
                    existing.Details = JToken.FromObject(existingDetails, ctx.Handler.Serializer);
                    updatedPaymentEntities.Add((existing, invoiceCtx.Invoice));
                }
            }

            if (updatedPaymentEntities.Any())
            {
                await _paymentService.UpdatePayments(updatedPaymentEntities.Select(t => t.Payment).ToList());
                foreach (var invoiceId in invoicesToReopen)
                {
                    var invoice = updatedPaymentEntities.First(t => t.invoice.Id == invoiceId).invoice;
                    await _invoiceRepository.UpdateInvoiceStatus(
                        invoiceId,
                        new InvoiceState(InvoiceStatus.Processing, invoice.ExceptionStatus));
                }
                foreach (var group in updatedPaymentEntities.GroupBy(e => e.invoice))
                {
                    _eventAggregator.Publish(new InvoiceNeedUpdateEvent(group.Key.Id));
                }
            }
        }

        // Five missed polls (~75s at the default 15s poll interval) before we
        // consider a Settled invoice's tx as truly dropped from the wallet.
        private const int DropDetectionThreshold = 5;

        // A settled invoice is terminal in BTCPay and drops out of
        // GetMonitoredInvoices. Keep recently received settled Zano payments in the
        // reconciliation set long enough to catch ordinary chain reorganizations,
        // without polling the complete invoice history forever.
        private static readonly TimeSpan SettledReconciliationWindow = TimeSpan.FromHours(24);

        private const int TxNotFoundRpcErrorCode = -14;

        private async Task<InvoiceEntity[]> GetReconciliationInvoices(
            PaymentMethodId paymentMethodId,
            CancellationToken cancellationToken)
        {
            var monitored = await _invoiceRepository.GetMonitoredInvoices(paymentMethodId, cancellationToken);
            var cutoff = DateTimeOffset.UtcNow.Subtract(SettledReconciliationWindow);
            string paymentMethod = paymentMethodId.ToString();
            string settled = InvoiceStatus.Settled.ToString();

            using var db = _invoiceRepository.DbContextFactory.CreateContext();
            var recentlySettledIds = await db.Payments
                .Where(p => p.PaymentMethodId == paymentMethod
                            && p.Status == PaymentStatus.Settled
                            && p.Created.HasValue
                            && p.Created.Value >= cutoff
                            && p.InvoiceData.Status == settled)
                .Select(p => p.InvoiceDataId)
                .Distinct()
                .ToArrayAsync(cancellationToken);

            if (recentlySettledIds.Length == 0)
            {
                return monitored;
            }

            var recentlySettled = await _invoiceRepository.GetInvoices(recentlySettledIds);
            return monitored
                .Concat(recentlySettled)
                .GroupBy(invoice => invoice.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        private async Task<bool?> TransactionExistsAsync(
            string cryptoCode,
            string transactionId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(transactionId)
                || !_zanoRpcProvider.DaemonRpcClients.TryGetValue(cryptoCode, out var daemonRpcClient))
            {
                return null;
            }

            try
            {
                var response = await daemonRpcClient.SendCommandAsync<GetTxDetailsRequest, GetTxDetailsResponse>(
                    "get_tx_details",
                    new GetTxDetailsRequest { TxHash = transactionId },
                    cancellationToken);
                return string.Equals(response?.Status, "OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonRpcClient.JsonRpcApiException ex) when (ex.Error?.Code == TxNotFoundRpcErrorCode)
            {
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Zano reconciliation could not verify transaction {Tx} via {CryptoCode} daemon",
                    transactionId, cryptoCode);
                return null;
            }
        }

        public static bool ShouldUnaccountMissingPayment(PaymentStatus status, int missingPollCount)
            => missingPollCount >= DropDetectionThreshold
               && status is PaymentStatus.Processing or PaymentStatus.Settled;

        public static bool ShouldReopenSettledInvoice(
            InvoiceStatus status,
            InvoiceExceptionStatus exceptionStatus)
            => status == InvoiceStatus.Settled
               && exceptionStatus != InvoiceExceptionStatus.Marked;

        private static string DropDetectionKey(string txHash, string paymentId, string assetId) =>
            $"{txHash}#{paymentId}#{assetId}";

        // UpdateExistingPaymentConfirmations was removed: it bumped confirmations from
        // stored BlockHeight + daemon currentHeight whenever the wallet returned no
        // matching candidate, which silently aged reorged-out or wallet-dropped
        // payments to Settled. Confirmations now update only via the candidate path
        // above — i.e. only when the wallet still reports the transaction.

        private async Task HandlePaymentData(string cryptoCode, decimal totalAmount, string paymentId,
            string txId, long confirmations, long blockHeight, long locktime, string assetId, InvoiceEntity invoice,
            List<(PaymentEntity Payment, InvoiceEntity invoice)> paymentsToUpdate)
        {
            var network = _networkProvider.GetNetwork(cryptoCode);
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
            var handler = (ZanoPaymentMethodHandler)_handlers[pmi];
            var promptDetails = handler.ParsePaymentPromptDetails(invoice.GetPaymentPrompt(pmi).Details);
            var details = new ZanoPaymentData()
            {
                PaymentId = paymentId,
                TransactionId = txId,
                ConfirmationCount = confirmations,
                BlockHeight = blockHeight,
                LockTime = locktime,
                InvoiceSettledConfirmationThreshold = promptDetails.InvoiceSettledConfirmationThreshold,
                AssetId = assetId ?? promptDetails.AssetId
            };
            var status = GetStatus(details, invoice.SpeedPolicy) ? PaymentStatus.Settled : PaymentStatus.Processing;
            var paymentData = new PaymentData()
            {
                Status = status,
                Amount = ZanoMoney.FromAtomic(totalAmount, network.Divisibility),
                Created = DateTimeOffset.UtcNow,
                Id = $"{txId}#{paymentId}",
                Currency = network.CryptoCode,
                InvoiceDataId = invoice.Id,
            }.Set(invoice, handler, details);

            // Check if this tx exists as a payment to this invoice already
            var alreadyExistingPayment = GetAllZanoPayments(invoice, cryptoCode)
                .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

            if (alreadyExistingPayment == null)
            {
                var payment = await _paymentService.AddPayment(paymentData, [txId]);
                if (payment != null)
                {
                    await ReceivedPayment(invoice, payment);
                }
            }
            else
            {
                // Update existing payment with new confirmation data
                alreadyExistingPayment.Status = status;
                alreadyExistingPayment.Details = JToken.FromObject(details, handler.Serializer);
                paymentsToUpdate.Add((alreadyExistingPayment, invoice));
            }
        }

        private bool GetStatus(ZanoPaymentData details, SpeedPolicy speedPolicy)
            => GetStatus(details, speedPolicy, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        public static bool GetStatus(ZanoPaymentData details, SpeedPolicy speedPolicy, long nowUnixSeconds)
            => !IsTimestampLocked(details, nowUnixSeconds)
               && !IsHeightLockedUnconfirmed(details)
               && ConfirmationsRequired(details, speedPolicy) <= details.ConfirmationCount;

        // A height-form unlock_time on a still-unconfirmed (mempool, BlockHeight==0) transfer
        // can't be proven satisfied: ConfirmationsRequired deliberately ignores the height
        // lockExtra while BlockHeight==0, so without this gate a HighSpeed/0-conf invoice
        // would settle on an output that is locked until an arbitrary future block. Once the
        // tx confirms (BlockHeight>0) the lockExtra path in ConfirmationsRequired takes over.
        public static bool IsHeightLockedUnconfirmed(ZanoPaymentData details)
            => details.LockTime > 0
               && details.LockTime < ZanoBlockHeightTimestampThreshold
               && details.BlockHeight == 0;

        // Zano `unlock_time` semantics (mirrors CryptoNote):
        //   0                 → output unlocked from the moment the tx is mined
        //   < 500_000_000     → absolute block height the output unlocks at
        //   ≥ 500_000_000     → Unix timestamp the output unlocks at
        // Most user-built txs use 0; the wallet only sets a non-zero unlock_time for
        // specific cases (mined coinbase, time-locked sends). The previous logic
        // compared ConfirmationCount directly against LockTime, which mis-treated an
        // absolute block height as a confirmation count and trapped invoices in
        // Processing forever (e.g. LockTime=13985 demanded ~13980 confs).
        private const long ZanoBlockHeightTimestampThreshold = 500_000_000L;

        // Timestamp-locked outputs are not spendable until wall-clock time reaches the
        // lock. ConfirmationsRequired() can't express "wait until time T" as a
        // confirmation count, so settlement is gated separately in GetStatus(). The
        // listener polling loop re-evaluates pending invoices on every tick, so the
        // status flips automatically once the timestamp passes.
        public static bool IsTimestampLocked(ZanoPaymentData details, long nowUnixSeconds)
            => details.LockTime >= ZanoBlockHeightTimestampThreshold
               && nowUnixSeconds < details.LockTime;

        public static long ConfirmationsRequired(ZanoPaymentData details, SpeedPolicy speedPolicy)
        {
            long baseRequired = details.InvoiceSettledConfirmationThreshold ?? speedPolicy switch
            {
                SpeedPolicy.HighSpeed => 0,
                SpeedPolicy.MediumSpeed => 1,
                SpeedPolicy.LowMediumSpeed => 2,
                SpeedPolicy.LowSpeed => 6,
                _ => 6,
            };

            long lockExtra = 0;
            if (details.LockTime > 0
                && details.LockTime < ZanoBlockHeightTimestampThreshold
                && details.BlockHeight > 0
                && details.LockTime >= details.BlockHeight)
            {
                lockExtra = details.LockTime - details.BlockHeight + 1;
            }

            return Math.Max(baseRequired, lockExtra);
        }

        // Collapse a flat list of (tx, pid, asset, amount, height, unlock) candidates to
        // one entry per (tx, pid, asset). A single tx can legally contain multiple income
        // subtransfers for the same asset_id and payment_id (sender wallet composing the
        // payment from several outputs to one integrated address), and the same tx can
        // also appear first as a mempool row (Height=0) and again as a confirmed row.
        // We keep the highest-height tier per (tx, pid, asset) — that resolves the
        // mempool→confirmed transition — and sum the amounts within that tier so a
        // multi-output payment is recorded at its full amount instead of one leg only.
        public static IReadOnlyList<(decimal Amount, string PaymentId, string AssetId, string TxHash, long Height, long UnlockTime)>
            AggregateCandidates(IEnumerable<(decimal Amount, string PaymentId, string AssetId, string TxHash, long Height, long UnlockTime)> candidates)
            => candidates
                .GroupBy(c => $"{c.TxHash}#{c.PaymentId}#{c.AssetId}", StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var maxHeight = g.Max(c => c.Height);
                    var tier = g.Where(c => c.Height == maxHeight).ToList();
                    return (
                        Amount: tier.Sum(c => c.Amount),
                        PaymentId: tier[0].PaymentId,
                        AssetId: tier[0].AssetId,
                        TxHash: tier[0].TxHash,
                        Height: maxHeight,
                        UnlockTime: tier.Max(c => c.UnlockTime));
                })
                .ToList();

        private IEnumerable<PaymentEntity> GetAllZanoPayments(InvoiceEntity invoice, string cryptoCode)
        {
            return invoice.GetPayments(false)
                .Where(p => p.PaymentMethodId == PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode));
        }

        // Pre-bundled per-crypto state used by the wallet-group scan. Carries the
        // network/handler lookup, the expanded pending-invoice records, and the set
        // of payment_ids we expect to find in the wallet transfer list.
        private sealed record CryptoScanContext(
            string CryptoCode,
            ZanoSpecificBtcPayNetwork Network,
            ZanoPaymentMethodHandler Handler,
            List<(InvoiceEntity Invoice,
                  IEnumerable<PaymentEntity> ExistingPayments,
                  PaymentPrompt Prompt,
                  ZanoOnChainPaymentMethodDetails PaymentMethodDetails,
                  HashSet<string> AllPaymentIds)> ExpandedInvoices,
            HashSet<string> PaymentIds);
    }
}
