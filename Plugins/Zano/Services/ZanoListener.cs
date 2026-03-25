using System;
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
        }

        protected override void SubscribeToEvents()
        {
            base.SubscribeToEvents();
            Subscribe<ZanoPollEvent>();
            Subscribe<ZanoRpcProvider.ZanoDaemonStateChange>();
        }

        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            if (evt is ZanoRpcProvider.ZanoDaemonStateChange stateChange)
            {
                if (_zanoRpcProvider.IsAvailable(stateChange.CryptoCode))
                {
                    _logger.LogInformation("{CryptoCode} just became available", stateChange.CryptoCode);
                    _ = UpdateAnyPendingZanoPayment(stateChange.CryptoCode);
                }
                else
                {
                    _logger.LogInformation("{CryptoCode} just became unavailable", stateChange.CryptoCode);
                }
            }
            else if (evt is ZanoPollEvent pollEvent)
            {
                if (_zanoRpcProvider.IsAvailable(pollEvent.CryptoCode))
                {
                    await UpdateAnyPendingZanoPayment(pollEvent.CryptoCode);
                }
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

        private async Task UpdatePaymentStates(string cryptoCode, InvoiceEntity[] invoices)
        {
            if (!invoices.Any())
            {
                return;
            }

            var walletRpcClient = _zanoRpcProvider.WalletRpcClients[cryptoCode];
            var network = _networkProvider.GetNetwork(cryptoCode);
            var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
            var handler = (ZanoPaymentMethodHandler)_handlers[paymentMethodId];

            // Get current daemon height for confirmation calculation
            long currentHeight = 0;
            if (_zanoRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary))
            {
                currentHeight = summary.CurrentHeight;
            }

            // Collect all payment_ids from pending invoices
            var expandedInvoices = invoices.Select(entity => (
                    Invoice: entity,
                    ExistingPayments: GetAllZanoPayments(entity, cryptoCode),
                    Prompt: entity.GetPaymentPrompt(paymentMethodId),
                    PaymentMethodDetails: handler.ParsePaymentPromptDetails(entity.GetPaymentPrompt(paymentMethodId).Details)))
                .ToList();

            var paymentIds = expandedInvoices
                .Select(e => e.PaymentMethodDetails.PaymentId)
                .Where(pid => !string.IsNullOrEmpty(pid))
                .Distinct()
                .ToList();

            if (!paymentIds.Any())
            {
                return;
            }

            // Batch query all payment_ids at once
            GetBulkPaymentsResponse result;
            try
            {
                result = await walletRpcClient.SendCommandAsync<GetBulkPaymentsRequest, GetBulkPaymentsResponse>(
                    "get_bulk_payments",
                    new GetBulkPaymentsRequest()
                    {
                        PaymentIds = paymentIds,
                        MinBlockHeight = 0,
                        AllowLockedTransactions = false
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query bulk payments for {CryptoCode}", cryptoCode);
                return;
            }

            if (result?.Payments == null || !result.Payments.Any())
            {
                // Still need to update existing payments for confirmation changes
                await UpdateExistingPaymentConfirmations(cryptoCode, invoices, handler, currentHeight);
                return;
            }

            var updatedPaymentEntities = new List<(PaymentEntity Payment, InvoiceEntity invoice)>();
            var processingTasks = new List<Task>();

            // Deduplicate: Zano returns both confirmed (block_height>0) and mempool (block_height=0)
            // entries for the same tx. Keep the confirmed entry when available.
            var dedupedPayments = result.Payments
                .GroupBy(p => $"{p.TxHash}#{p.PaymentId}")
                .Select(g => g.OrderByDescending(p => p.BlockHeight).First())
                .ToList();

            foreach (var payment in dedupedPayments)
            {
                // Find the invoice matching this payment_id
                var matchingInvoice = expandedInvoices
                    .FirstOrDefault(e => e.PaymentMethodDetails.PaymentId == payment.PaymentId);

                if (matchingInvoice.Invoice == null)
                {
                    continue;
                }

                // Calculate confirmations from daemon height
                long confirmations = payment.BlockHeight > 0 && currentHeight > 0
                    ? currentHeight - payment.BlockHeight + 1
                    : 0;

                processingTasks.Add(HandlePaymentData(
                    cryptoCode,
                    payment.Amount,
                    payment.PaymentId,
                    payment.TxHash,
                    confirmations,
                    payment.BlockHeight,
                    payment.UnlockTime,
                    matchingInvoice.Invoice,
                    updatedPaymentEntities));
            }

            await Task.WhenAll(processingTasks);

            if (updatedPaymentEntities.Any())
            {
                await _paymentService.UpdatePayments(updatedPaymentEntities.Select(t => t.Payment).ToList());
                foreach (var group in updatedPaymentEntities.GroupBy(e => e.invoice))
                {
                    _eventAggregator.Publish(new InvoiceNeedUpdateEvent(group.Key.Id));
                }
            }
        }

        private async Task UpdateExistingPaymentConfirmations(string cryptoCode, InvoiceEntity[] invoices,
            ZanoPaymentMethodHandler handler, long currentHeight)
        {
            if (currentHeight <= 0)
            {
                return;
            }

            var updatedPayments = new List<(PaymentEntity Payment, InvoiceEntity invoice)>();

            foreach (var invoice in invoices)
            {
                var existingPayments = GetAllZanoPayments(invoice, cryptoCode);
                foreach (var payment in existingPayments)
                {
                    var data = handler.ParsePaymentDetails(payment.Details);
                    if (data.BlockHeight > 0)
                    {
                        var newConfirmations = currentHeight - data.BlockHeight + 1;
                        if (newConfirmations != data.ConfirmationCount)
                        {
                            data.ConfirmationCount = newConfirmations;
                            var status = GetStatus(data, invoice.SpeedPolicy) ? PaymentStatus.Settled : PaymentStatus.Processing;
                            payment.Status = status;
                            payment.Details = JToken.FromObject(data, handler.Serializer);
                            updatedPayments.Add((payment, invoice));
                        }
                    }
                }
            }

            if (updatedPayments.Any())
            {
                await _paymentService.UpdatePayments(updatedPayments.Select(t => t.Payment).ToList());
                foreach (var group in updatedPayments.GroupBy(e => e.invoice))
                {
                    _eventAggregator.Publish(new InvoiceNeedUpdateEvent(group.Key.Id));
                }
            }
        }

        private async Task HandlePaymentData(string cryptoCode, long totalAmount, string paymentId,
            string txId, long confirmations, long blockHeight, long locktime, InvoiceEntity invoice,
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
                InvoiceSettledConfirmationThreshold = promptDetails.InvoiceSettledConfirmationThreshold
            };
            var status = GetStatus(details, invoice.SpeedPolicy) ? PaymentStatus.Settled : PaymentStatus.Processing;
            var paymentData = new PaymentData()
            {
                Status = status,
                Amount = ZanoMoney.Convert(totalAmount),
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
            => ConfirmationsRequired(details, speedPolicy) <= details.ConfirmationCount;

        public static long ConfirmationsRequired(ZanoPaymentData details, SpeedPolicy speedPolicy)
            => (details, speedPolicy) switch
            {
                (_, _) when details.ConfirmationCount < details.LockTime =>
                    details.LockTime - details.ConfirmationCount,
                ({ InvoiceSettledConfirmationThreshold: long v }, _) => v,
                (_, SpeedPolicy.HighSpeed) => 0,
                (_, SpeedPolicy.MediumSpeed) => 1,
                (_, SpeedPolicy.LowMediumSpeed) => 2,
                (_, SpeedPolicy.LowSpeed) => 6,
                _ => 6,
            };

        private async Task UpdateAnyPendingZanoPayment(string cryptoCode)
        {
            var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var invoices = await _invoiceRepository.GetMonitoredInvoices(paymentMethodId);
            if (!invoices.Any())
            {
                return;
            }
            invoices = invoices.Where(entity => entity.GetPaymentPrompt(paymentMethodId)?.Activated is true).ToArray();
            await UpdatePaymentStates(cryptoCode, invoices);
        }

        private IEnumerable<PaymentEntity> GetAllZanoPayments(InvoiceEntity invoice, string cryptoCode)
        {
            return invoice.GetPayments(false)
                .Where(p => p.PaymentMethodId == PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode));
        }
    }
}
