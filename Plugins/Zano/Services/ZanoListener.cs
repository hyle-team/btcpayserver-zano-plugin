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
using BTCPayServer.Services.Notifications;

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
        private readonly NotificationSender _notificationSender;

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
            PaymentService paymentService,
            NotificationSender notificationSender) : base(eventAggregator, logger)
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
            _notificationSender = notificationSender;

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
            var notifications = new List<ZanoPaymentReconciliationNotification>();

            var dedupedCandidates = AggregateCandidates(candidates);

            // Candidates are processed SEQUENTIALLY. They share the update list and the
            // notification list; concurrent tasks resuming after an await would race on
            // them (the previous fire-and-WhenAll shape only worked because the update
            // branch happened to have no await before its Add).
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

                await HandlePaymentData(
                    ctx.CryptoCode,
                    cand.Amount,
                    cand.PaymentId,
                    cand.TxHash,
                    confirmations,
                    cand.Height,
                    cand.UnlockTime,
                    cand.AssetId,
                    matchingInvoice.Invoice,
                    updatedPaymentEntities,
                    notifications);
            }

            // A history-window miss is never enough to invalidate a payment. Two
            // independent sources must agree before accounting state changes:
            //   1. the daemon's targeted get_tx_details says NOT FOUND (-14: absent from
            //      chain AND pool), and
            //   2. the wallet's targeted get_bulk_payments for the payment id no longer
            //      lists the transaction. The wallet's payment index covers chain AND
            //      pool: wallet2::prepare_wti populates m_payments from both
            //      handle_money (in-block) and handle_unconfirmed_tx (pool), and
            //      detach_blockchain drops reorged entries until they are re-seen.
            // Daemon and wallet are independently configured endpoints whose views can
            // legitimately diverge (different nodes, a stale tip, pool eviction the wallet
            // hasn't processed); either one alone is not evidence. Anything short of both
            // agreeing is inconclusive and leaves the payment untouched, and a wallet-
            // positive answer RESETS the miss counter — the threshold means consecutive
            // joint misses, not accumulated ones.
            //
            // Scope: only SETTLED payments are drop-checked (see
            // ShouldUnaccountMissingPayment) and only the payment row changes — the
            // invoice is never rewritten. BTCPay's invoice state machine has no valid
            // path out of Settled: a raw Settled→Processing write decays to New and
            // then Expired on the next watcher pass when the invoice is underpaid, or
            // bounces back to Settled replaying Confirmed/Completed webhooks when
            // other payments still cover it. The merchant-facing signal is a
            // store-scoped dashboard notification (delivered BEFORE the payment write and
            // retried on later passes until the store has it) plus the critical log.
            var matchedKeys = new HashSet<string>(
                dedupedCandidates.Select(c => DropDetectionKey(c.TxHash, c.PaymentId, c.AssetId)),
                StringComparer.OrdinalIgnoreCase);
            var probeCircuitOpen = false;
            var now = DateTimeOffset.UtcNow;
            var probeBudget = System.Diagnostics.Stopwatch.StartNew();

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

                    // Unconfirmed payments are never probed: absence is ordinary
                    // mempool churn and no action would be taken either way.
                    if (existing.Status == PaymentStatus.Processing)
                    {
                        continue;
                    }

                    // A loss alert that could not be persisted last time is owed
                    // regardless of what the probes say now.
                    if (existing.Status == PaymentStatus.Unaccounted && !existingDetails.LossNotified)
                    {
                        if (await SendReconciliationNotificationAsync(BuildNotification(
                                invoiceCtx.Invoice, existingDetails,
                                ZanoPaymentReconciliationNotification.Kind.PaymentLost)))
                        {
                            existingDetails.LossNotified = true;
                            existing.Details = JToken.FromObject(existingDetails, ctx.Handler.Serializer);
                            updatedPaymentEntities.Add((existing, invoiceCtx.Invoice));
                        }
                    }

                    // One inconclusive probe (transport failure, wedged endpoint,
                    // malformed reply) means the endpoint can't be trusted for the
                    // rest of this pass. Skipping the remaining probes is free — an
                    // inconclusive result changes no state — and it keeps a dead
                    // endpoint's timeout+retry budget from stalling the whole wallet
                    // scan once per unmatched payment. The wall-clock budget bounds the
                    // slow-but-conclusive case the same way: probes are reconciliation
                    // work and must not hold the live wallet-scan lock indefinitely;
                    // whatever is not reached this pass is simply retried next pass.
                    if (probeCircuitOpen || probeBudget.Elapsed > ProbeBudgetPerPass)
                    {
                        continue;
                    }

                    // Cached positives are not trusted in the tail of the reconciliation
                    // window (a drop right after a cache fill there could ride cached
                    // "exists" answers until the invoice ages out) and never for recovery
                    // decisions, which need the daemon's real chain placement.
                    var allowCache = existing.Status != PaymentStatus.Unaccounted
                                     && !IsInReconciliationTail(existingDetails, existing.ReceivedTime, now);
                    var probe = await ProbeDaemonAsync(
                        ctx.CryptoCode, existingDetails.TransactionId, allowCache, cancellationToken);
                    if (probe.Exists is null)
                    {
                        probeCircuitOpen = true;
                        continue;
                    }

                    if (existing.Status == PaymentStatus.Unaccounted)
                    {
                        // Recovery: the transaction is back on the daemon (rebroadcast,
                        // re-mined after a reorg, or the earlier unaccounting was wrong).
                        // Its confirmations are recomputed from the daemon's own chain
                        // placement — never from the pre-drop record — so a pool-only
                        // reappearance restores to Processing, not straight to Settled.
                        if (probe.Exists is true)
                        {
                            ApplyRecoveredPlacement(existingDetails, probe.KeeperBlock, currentHeight);
                            existing.Status = GetStatus(existingDetails, invoiceCtx.Invoice.SpeedPolicy)
                                ? PaymentStatus.Settled
                                : PaymentStatus.Processing;
                            if (existing.Status == PaymentStatus.Settled)
                            {
                                existingDetails.SettledAt ??= now.ToUnixTimeSeconds();
                            }
                            existingDetails.MissingPollCount = 0;
                            // Restoration is announced before the write; if the write then
                            // fails the merchant sees a benign early "restored" and the next
                            // pass restores again under the same episode identifier.
                            await SendReconciliationNotificationAsync(BuildNotification(
                                invoiceCtx.Invoice, existingDetails,
                                ZanoPaymentReconciliationNotification.Kind.PaymentRestored,
                                episode: now.ToUnixTimeSeconds().ToString()));
                            existing.Details = JToken.FromObject(existingDetails, ctx.Handler.Serializer);
                            updatedPaymentEntities.Add((existing, invoiceCtx.Invoice));
                            _logger.LogWarning(
                                "Zano reconciliation: transaction reappeared on daemon (keeper_block={KeeperBlock}); restoring payment to {Status}; invoice={InvoiceId} pid={Pid} tx={Tx}",
                                probe.KeeperBlock, existing.Status, invoiceCtx.Invoice.Id, existingDetails.PaymentId, existingDetails.TransactionId);
                        }
                        continue;
                    }

                    if (probe.Exists is true)
                    {
                        if (existingDetails.MissingPollCount != 0)
                        {
                            existingDetails.MissingPollCount = 0;
                            existing.Details = JToken.FromObject(existingDetails, ctx.Handler.Serializer);
                            updatedPaymentEntities.Add((existing, invoiceCtx.Invoice));
                        }
                        continue;
                    }

                    // Daemon says gone. Require the wallet to agree via a targeted
                    // lookup before it counts as a miss.
                    var walletHasTx = await WalletStillListsTransactionAsync(
                        ctx.CryptoCode, existingDetails.PaymentId, existingDetails.TransactionId, cancellationToken);
                    if (walletHasTx is null)
                    {
                        // Wallet lookup failed: inconclusive; don't count, don't stall
                        // the rest of the pass on the wallet either.
                        probeCircuitOpen = true;
                        continue;
                    }
                    if (walletHasTx is true)
                    {
                        _logger.LogWarning(
                            "Zano reconciliation: daemon reports tx {Tx} not found but the wallet still lists it for pid={Pid} — endpoints diverge; leaving payment untouched (invoice={InvoiceId})",
                            existingDetails.TransactionId, existingDetails.PaymentId, invoiceCtx.Invoice.Id);
                        if (existingDetails.MissingPollCount != 0)
                        {
                            // Not a joint miss: the consecutive-miss sequence is broken.
                            existingDetails.MissingPollCount = 0;
                            existing.Details = JToken.FromObject(existingDetails, ctx.Handler.Serializer);
                            updatedPaymentEntities.Add((existing, invoiceCtx.Invoice));
                        }
                        continue;
                    }

                    // Both sources agree the settled payment's transaction is gone.
                    existingDetails.MissingPollCount++;
                    if (ShouldUnaccountMissingPayment(existing.Status, existingDetails.MissingPollCount))
                    {
                        existing.Status = PaymentStatus.Unaccounted;
                        existingDetails.UnaccountedAt = now.ToUnixTimeSeconds();
                        // ConfirmationCount/BlockHeight are deliberately kept: they preserve
                        // the last known chain position for the merchant's review.
                        // Alert BEFORE the write so a crash between the two leaves an
                        // alert without state (benign, self-correcting) rather than state
                        // without an alert (silent). LossNotified records the outcome so an
                        // unpersisted alert is retried on later passes.
                        existingDetails.LossNotified = await SendReconciliationNotificationAsync(BuildNotification(
                            invoiceCtx.Invoice, existingDetails,
                            ZanoPaymentReconciliationNotification.Kind.PaymentLost));
                        _logger.LogCritical(
                            "Zano reconciliation: settled payment lost its transaction (absent from daemon chain+pool and from wallet after {Threshold} checks); payment marked Unaccounted but invoice {InvoiceId} REMAINS SETTLED — BTCPay cannot un-settle an invoice. Merchant notified={Notified}. pid={Pid} tx={Tx}",
                            DropDetectionThreshold, invoiceCtx.Invoice.Id, existingDetails.LossNotified, existingDetails.PaymentId, existingDetails.TransactionId);
                    }
                    existing.Details = JToken.FromObject(existingDetails, ctx.Handler.Serializer);
                    updatedPaymentEntities.Add((existing, invoiceCtx.Invoice));
                }
            }

            // Notifications collected by the candidate path (confirmation regressions)
            // go out before the write for the same reason as above.
            foreach (var n in notifications)
            {
                await SendReconciliationNotificationAsync(n);
            }

            if (updatedPaymentEntities.Any())
            {
                await _paymentService.UpdatePayments(updatedPaymentEntities.Select(t => t.Payment).ToList());
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
        // GetMonitoredInvoices. Keep recently settled Zano payments in the
        // reconciliation set long enough to catch ordinary chain reorganizations,
        // without polling the complete invoice history forever.
        //
        // Anchored on the payment's FIRST settlement time (ZanoPaymentData.SettledAt),
        // i.e. the earliest moment a merchant could have shipped, so protection is
        // measured from settlement rather than from first detection. Rows written
        // before SettledAt existed fall back to the row's Created time.
        private static readonly TimeSpan SettledReconciliationWindow = TimeSpan.FromHours(48);

        // After a payment goes Unaccounted it stays eligible for daemon recovery for
        // this long (measured from UnaccountedAt), independent of the settlement window.
        private static readonly TimeSpan UnaccountedRecoveryWindow = TimeSpan.FromDays(7);

        // The SQL side can only see the row's Created and Status columns (SettledAt,
        // MissingPollCount, UnaccountedAt live inside the payment blob), so candidates
        // are pre-filtered by Created over a bounded look-back and the per-payment
        // reconciliation state is applied in memory. Settlement lagging first detection
        // by more than this is out of scope of reconciliation.
        private static readonly TimeSpan ReconciliationLookback = TimeSpan.FromDays(30);

        // The eligible-invoice set is recomputed at most this often per payment method:
        // the look-back query returns every settled payment's blob and does not need
        // 15-second freshness. Payments already selected stay selected between refreshes
        // (their in-flight state lives in the blob and re-qualifies them at the next
        // refresh), and a fresh settlement enters within one refresh interval.
        private static readonly TimeSpan ReconciliationSelectionTtl = TimeSpan.FromSeconds(60);
        private readonly ConcurrentDictionary<string, (DateTimeOffset At, string[] InvoiceIds)> _reconciliationSelection = new(StringComparer.OrdinalIgnoreCase);

        // Wall-clock cap on reconciliation probing per pass while the wallet-scan lock is
        // held. Whatever is not reached is retried next pass; nothing is decided from a
        // skipped probe.
        private static readonly TimeSpan ProbeBudgetPerPass = TimeSpan.FromSeconds(20);

        private const int TxNotFoundRpcErrorCode = -14;

        // Positive get_tx_details results are cached briefly: a settled payment that
        // has aged past the wallet scan's page cap would otherwise be re-verified
        // against the daemon on every poll for the whole reconciliation window. A
        // cached hit delays detection of a genuine drop by at most the TTL plus the
        // threshold polls — except in the window tail, where the cache is bypassed
        // (see IsInReconciliationTail).
        private static readonly TimeSpan DaemonHitCacheTtl = TimeSpan.FromMinutes(5);
        private const int DaemonHitCacheMaxEntries = 512;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _daemonHitCache = new(StringComparer.OrdinalIgnoreCase);

        // Approximate poll cadence, used only to size the cache-bypass tail.
        private static readonly TimeSpan ApproxPollInterval = TimeSpan.FromSeconds(15);

        private void CacheDaemonHit(string transactionId)
        {
            if (_daemonHitCache.Count >= DaemonHitCacheMaxEntries)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var stale in _daemonHitCache.Where(kv => now - kv.Value >= DaemonHitCacheTtl).Select(kv => kv.Key).ToList())
                {
                    _daemonHitCache.TryRemove(stale, out _);
                }
            }
            _daemonHitCache[transactionId] = DateTimeOffset.UtcNow;
        }

        private async Task<InvoiceEntity[]> GetReconciliationInvoices(
            PaymentMethodId paymentMethodId,
            CancellationToken cancellationToken)
        {
            var monitored = await _invoiceRepository.GetMonitoredInvoices(paymentMethodId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            string paymentMethod = paymentMethodId.ToString();

            string[] eligibleIds;
            if (_reconciliationSelection.TryGetValue(paymentMethod, out var cached)
                && now - cached.At < ReconciliationSelectionTtl)
            {
                eligibleIds = cached.InvoiceIds;
            }
            else
            {
                eligibleIds = await SelectReconciliationInvoiceIds(paymentMethodId, now, cancellationToken);
                _reconciliationSelection[paymentMethod] = (now, eligibleIds);
            }

            if (eligibleIds.Length == 0)
            {
                return monitored;
            }

            InvoiceEntity[] recentlySettled;
            try
            {
                recentlySettled = await _invoiceRepository.GetInvoices(eligibleIds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Zano reconciliation: loading settled invoices failed; continuing with monitored invoices only for {PaymentMethod}",
                    paymentMethod);
                return monitored;
            }

            return monitored
                .Concat(recentlySettled)
                .GroupBy(invoice => invoice.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        // Which SETTLED invoices still need reconciliation. Decided per PAYMENT from
        // durable state in the payment row (status column + blob) — never from
        // in-process memory — so a restart, a healthy sibling payment, or an idle hour
        // cannot strand a payment mid-sequence.
        private async Task<string[]> SelectReconciliationInvoiceIds(
            PaymentMethodId paymentMethodId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            string paymentMethod = paymentMethodId.ToString();
            string settled = InvoiceStatus.Settled.ToString();
            var handler = (ZanoPaymentMethodHandler)_handlers[paymentMethodId];
            var lookbackCutoff = now.Subtract(ReconciliationLookback);
            try
            {
                using var db = _invoiceRepository.DbContextFactory.CreateContext();
                var rows = await db.Payments
                    .Where(p => p.PaymentMethodId == paymentMethod
                                && p.Created.HasValue
                                && p.Created.Value >= lookbackCutoff
                                && p.InvoiceData.Status == settled)
                    .Select(p => new { p.InvoiceDataId, p.Created, p.Status, p.Blob2 })
                    .ToArrayAsync(cancellationToken);

                var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    ZanoPaymentData details = null;
                    try
                    {
                        var token = row.Blob2 is null ? null : JObject.Parse(row.Blob2)["details"];
                        details = token?.ToObject<ZanoPaymentData>(handler.Serializer);
                    }
                    catch (Exception)
                    {
                        // Unparseable blob: fall back to the Created anchor below.
                    }
                    if (NeedsReconciliation(row.Status, details, row.Created.Value, now))
                    {
                        eligible.Add(row.InvoiceDataId);
                    }
                }
                return eligible.ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // This side-query reaches into core's EF model. If a core schema or
                // mapping change ever breaks it, degrade to monitored-only scanning
                // instead of killing ALL Zano payment detection for the network.
                _logger.LogError(ex,
                    "Zano reconciliation query failed; continuing with monitored invoices only for {PaymentMethod}",
                    paymentMethod);
                return Array.Empty<string>();
            }
        }

        // Per-payment eligibility, from durable state only:
        //  - inside the settlement window (SettledAt, or Created for old rows), or
        //  - an in-flight sub-threshold miss sequence (never abandoned at the boundary), or
        //  - Unaccounted and inside the recovery window, or
        //  - Unaccounted with the loss alert still undelivered.
        public static bool NeedsReconciliation(PaymentStatus? status, ZanoPaymentData details, DateTimeOffset created, DateTimeOffset now)
        {
            if (IsWithinReconciliationWindow(details?.SettledAt, created, now))
            {
                return true;
            }
            if (details == null)
            {
                return false;
            }
            if (details.MissingPollCount > 0 && status == PaymentStatus.Settled)
            {
                return true;
            }
            if (status == PaymentStatus.Unaccounted)
            {
                if (!details.LossNotified)
                {
                    return true;
                }
                if (details.UnaccountedAt.HasValue
                    && now - DateTimeOffset.FromUnixTimeSeconds(details.UnaccountedAt.Value) <= UnaccountedRecoveryWindow)
                {
                    return true;
                }
            }
            return false;
        }

        // Window anchor: first settlement time when known, else the row's creation.
        public static bool IsWithinReconciliationWindow(long? settledAtUnix, DateTimeOffset created, DateTimeOffset now)
        {
            var anchor = settledAtUnix.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(settledAtUnix.Value)
                : created;
            return now - anchor <= SettledReconciliationWindow;
        }

        // The last (cache TTL + threshold polls) of the window: cached daemon positives
        // are not trusted here, so a drop can still accumulate enough conclusive misses
        // before eligibility ends. Also true once the window has already passed (rows
        // kept eligible by an in-flight sequence) — nothing to protect by caching there.
        public static bool IsInReconciliationTail(ZanoPaymentData details, DateTimeOffset created, DateTimeOffset now)
        {
            var anchor = details.SettledAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(details.SettledAt.Value)
                : created;
            var windowEnd = anchor + SettledReconciliationWindow;
            var tail = DaemonHitCacheTtl + (ApproxPollInterval * DropDetectionThreshold);
            return windowEnd - now <= tail;
        }

        private readonly record struct DaemonProbeResult(bool? Exists, long? KeeperBlock);

        private async Task<DaemonProbeResult> ProbeDaemonAsync(
            string cryptoCode,
            string transactionId,
            bool allowCache,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(transactionId)
                || !_zanoRpcProvider.DaemonRpcClients.TryGetValue(cryptoCode, out var daemonRpcClient))
            {
                return new DaemonProbeResult(null, null);
            }

            if (allowCache
                && _daemonHitCache.TryGetValue(transactionId, out var seenAt)
                && DateTimeOffset.UtcNow - seenAt < DaemonHitCacheTtl)
            {
                // Cache only answers "exists"; placement is unknown from cache. The
                // recovery path (the only consumer of KeeperBlock) passes
                // allowCache=false, so it always sees a real answer.
                return new DaemonProbeResult(true, null);
            }

            try
            {
                var response = await daemonRpcClient.SendCommandAsync<GetTxDetailsRequest, GetTxDetailsResponse>(
                    "get_tx_details",
                    new GetTxDetailsRequest { TxHash = transactionId },
                    cancellationToken);
                var classified = ClassifyTxDetailsResponse(response);
                if (classified is true)
                {
                    CacheDaemonHit(transactionId);
                    return new DaemonProbeResult(true, response.TxInfo.KeeperBlock);
                }
                _logger.LogWarning(
                    "Zano reconciliation: inconclusive get_tx_details reply for {Tx} via {CryptoCode} (status={Status}, hasTxInfo={HasTxInfo})",
                    transactionId, cryptoCode, response?.Status ?? "(null)", response?.TxInfo != null);
                return new DaemonProbeResult(null, null);
            }
            catch (JsonRpcClient.JsonRpcApiException ex) when (ex.Error?.Code == TxNotFoundRpcErrorCode)
            {
                return new DaemonProbeResult(false, null);
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
                return new DaemonProbeResult(null, null);
            }
        }

        // Targeted wallet-side presence check, independent of the paged history scan.
        // get_bulk_payments ignores asset_id when matching (amount is 0 for CA legs) but
        // lists the tx_hash per payment id, which is all we need; its index covers chain
        // and pool (see the comment at the drop-detection loop).
        // true = wallet still has the tx; false = wallet has no such payment/tx;
        // null = could not determine.
        private async Task<bool?> WalletStillListsTransactionAsync(
            string cryptoCode,
            string paymentId,
            string transactionId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(paymentId)
                || string.IsNullOrWhiteSpace(transactionId)
                || !_zanoRpcProvider.WalletRpcClients.TryGetValue(cryptoCode, out var walletRpcClient))
            {
                return null;
            }
            try
            {
                var response = await walletRpcClient.SendCommandAsync<GetBulkPaymentsRequest, GetBulkPaymentsResponse>(
                    "get_bulk_payments",
                    new GetBulkPaymentsRequest
                    {
                        PaymentIds = [paymentId],
                        MinBlockHeight = 0,
                        AllowLockedTransactions = true
                    },
                    cancellationToken);
                return ClassifyBulkPaymentsResponse(response, transactionId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Zano reconciliation could not query wallet for pid={Pid} via {CryptoCode}",
                    paymentId, cryptoCode);
                return null;
            }
        }

        // Tri-state for get_bulk_payments: a NULL result object (JSON-RPC result missing
        // or unparseable) is inconclusive. A result object WITHOUT a payments key is a
        // genuine empty answer — Zano's epee serializer omits empty containers
        // (keyvalue_serialization_overloads.h: `if(!container.size()) return true;`) —
        // so it is conclusive "not listed". Otherwise the tx hash must appear.
        public static bool? ClassifyBulkPaymentsResponse(GetBulkPaymentsResponse response, string transactionId)
        {
            if (response == null)
            {
                return null;
            }
            if (response.Payments == null)
            {
                return false;
            }
            return response.Payments.Any(p =>
                string.Equals(p.TxHash, transactionId, StringComparison.OrdinalIgnoreCase));
        }

        // Rewrite a recovered payment's chain placement from the daemon's answer.
        // keeper_block > 0: confirmed at that height → confirmations from the current
        // tip. keeper_block == -1 (or unknown): pool only → zero confirmations, so the
        // row can at most be Processing until the wallet sees it confirm.
        public static void ApplyRecoveredPlacement(ZanoPaymentData details, long? keeperBlock, long currentHeight)
        {
            if (keeperBlock is > 0 && currentHeight > 0)
            {
                details.BlockHeight = keeperBlock.Value;
                details.ConfirmationCount = Math.Max(0L, currentHeight - keeperBlock.Value);
            }
            else
            {
                details.BlockHeight = 0;
                details.ConfirmationCount = 0;
            }
        }

        // Episode identity: a loss is identified by its UnaccountedAt; a regression by
        // the observed placement (height/confirmations) so repeated flapping re-alerts
        // rather than staying silent; callers pass an explicit episode for restores.
        private static ZanoPaymentReconciliationNotification BuildNotification(
            InvoiceEntity invoice,
            ZanoPaymentData details,
            ZanoPaymentReconciliationNotification.Kind kind,
            string episode = null)
            => new()
            {
                StoreId = invoice.StoreId,
                InvoiceId = invoice.Id,
                TransactionId = details.TransactionId,
                Confirmations = details.ConfirmationCount,
                BlockHeight = details.BlockHeight,
                EventKind = kind,
                Episode = episode ?? kind switch
                {
                    ZanoPaymentReconciliationNotification.Kind.PaymentLost
                        => (details.UnaccountedAt ?? 0).ToString(),
                    ZanoPaymentReconciliationNotification.Kind.ConfirmationsRegressed
                        => $"h{details.BlockHeight}c{details.ConfirmationCount}",
                    _ => "0"
                }
            };

        // Returns whether the notification store accepted the notification. Never throws:
        // notification delivery must not break payment processing, but the outcome is
        // reported so the caller can record that a loss alert is still owed.
        private async Task<bool> SendReconciliationNotificationAsync(ZanoPaymentReconciliationNotification n)
        {
            try
            {
                await _notificationSender.SendNotification(new StoreScope(n.StoreId), n);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Zano reconciliation: failed to send {Kind} notification for invoice {InvoiceId}",
                    n.EventKind, n.InvoiceId);
                return false;
            }
        }

        // Only SETTLED payments are ever unaccounted by drop-detection. A Processing
        // (unconfirmed) payment absent from wallet and daemon is ordinary mempool churn
        // — the wallet rebroadcasts, or the invoice ages out via BTCPay's monitoring
        // expiry. Acting on it would revert a still-payable invoice to New and invite
        // the customer to pay a second time while the original tx can still confirm.
        public static bool ShouldUnaccountMissingPayment(PaymentStatus status, int missingPollCount)
            => missingPollCount >= DropDetectionThreshold
               && status is PaymentStatus.Settled;

        // Classification contract for get_tx_details replies: TRUE only for a
        // structurally valid OK response (status OK AND tx_info present). FALSE is
        // never returned from here — the only conclusive evidence of absence is the
        // daemon's explicit -14 not-found error, handled by the caller's exception
        // filter. A null/absent result, a non-OK status, or an OK reply without its
        // promised tx_info proves nothing: treating any of them as "gone" would let a
        // misbehaving proxy or RPC-layer change unaccount settled payments after
        // ~75s of degraded replies.
        public static bool? ClassifyTxDetailsResponse(GetTxDetailsResponse response)
            => string.Equals(response?.Status, "OK", StringComparison.OrdinalIgnoreCase)
               && response.TxInfo != null
                ? true
                : null;

        private static string DropDetectionKey(string txHash, string paymentId, string assetId) =>
            $"{txHash}#{paymentId}#{assetId}";

        // UpdateExistingPaymentConfirmations was removed: it bumped confirmations from
        // stored BlockHeight + daemon currentHeight whenever the wallet returned no
        // matching candidate, which silently aged reorged-out or wallet-dropped
        // payments to Settled. Confirmations now update only via the candidate path
        // above — i.e. only when the wallet still reports the transaction.

        // Called sequentially per candidate (see the caller). Appends to the shared
        // update/notification lists; the caller sends notifications and commits.
        private async Task HandlePaymentData(string cryptoCode, decimal totalAmount, string paymentId,
            string txId, long confirmations, long blockHeight, long locktime, string assetId, InvoiceEntity invoice,
            List<(PaymentEntity Payment, InvoiceEntity invoice)> paymentsToUpdate,
            List<ZanoPaymentReconciliationNotification> notifications)
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
            if (status == PaymentStatus.Settled)
            {
                details.SettledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
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
                // Funds that actually arrived are recorded even on a settled invoice
                // (late first observation, overpayment, replacement send): the row is
                // idempotent by id, so this fires once per transaction, and BTCPay's
                // payment history / API / refund flow must reflect real receipts. The
                // invoice itself is terminal and does not change.
                if (invoice.Status == InvoiceStatus.Settled)
                {
                    _logger.LogInformation(
                        "Zano: recording late transfer {Tx} ({Amount} atomic) to settled invoice {InvoiceId}",
                        txId, totalAmount, invoice.Id);
                }
                var payment = await _paymentService.AddPayment(paymentData, [txId]);
                if (payment != null)
                {
                    await ReceivedPayment(invoice, payment);
                }
            }
            else
            {
                var oldDetails = handler.ParsePaymentDetails(alreadyExistingPayment.Details);
                // Preserve the ORIGINAL settlement anchor and the loss-episode fields
                // across rewrites; only stamp SettledAt when the row settles for the
                // first time.
                details.SettledAt = oldDetails?.SettledAt
                    ?? (status == PaymentStatus.Settled ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null);
                details.UnaccountedAt = oldDetails?.UnaccountedAt;
                details.LossNotified = oldDetails?.LossNotified ?? false;
                details.MissingPollCount = 0;

                if (invoice.Status == InvoiceStatus.Settled)
                {
                    // On a terminal invoice only a status transition is worth a write.
                    // Tracking routine confirmation growth would rewrite the payment
                    // row and wake the invoice watcher every poll for the entire
                    // reconciliation window. The MissingPollCount check keeps the
                    // wallet-rematch path able to clear a partial miss-sequence.
                    if (alreadyExistingPayment.Status == status && (oldDetails?.MissingPollCount ?? 0) == 0)
                    {
                        return;
                    }
                    if (alreadyExistingPayment.Status == PaymentStatus.Settled && status == PaymentStatus.Processing)
                    {
                        _logger.LogCritical(
                            "Zano reconciliation: payment on settled invoice {InvoiceId} fell below its confirmation policy (reorg?); payment downgraded to Processing but the invoice REMAINS SETTLED — BTCPay cannot un-settle an invoice. Merchant notified. tx={Tx} confirmations={Confirmations}",
                            invoice.Id, txId, confirmations);
                        notifications.Add(BuildNotification(
                            invoice, details, ZanoPaymentReconciliationNotification.Kind.ConfirmationsRegressed));
                    }
                }
                if (alreadyExistingPayment.Status == PaymentStatus.Unaccounted)
                {
                    // Wallet-driven recovery: the wallet lists the transaction again, so
                    // the row leaves Unaccounted with placement taken from the wallet.
                    _logger.LogWarning(
                        "Zano reconciliation: transaction reappeared in wallet history; restoring payment to {Status}; invoice={InvoiceId} tx={Tx} h={Height} conf={Confirmations}",
                        status, invoice.Id, txId, blockHeight, confirmations);
                    notifications.Add(BuildNotification(
                        invoice, details, ZanoPaymentReconciliationNotification.Kind.PaymentRestored,
                        episode: DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
                }
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
