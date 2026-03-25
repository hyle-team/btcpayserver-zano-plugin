using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Zano.Configuration;
using BTCPayServer.Plugins.Zano.Payments;
using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace BTCPayServer.Plugins.Zano.Controllers
{
    [Route("stores/{storeId}/zano")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public class UIZanoStoreController : Controller
    {
        private readonly ZanoConfiguration _zanoConfiguration;
        private readonly StoreRepository _storeRepository;
        private readonly ZanoRpcProvider _zanoRpcProvider;
        private readonly PaymentMethodHandlerDictionary _handlers;
        private IStringLocalizer StringLocalizer { get; }

        public UIZanoStoreController(ZanoConfiguration zanoConfiguration,
            StoreRepository storeRepository, ZanoRpcProvider zanoRpcProvider,
            PaymentMethodHandlerDictionary handlers,
            IStringLocalizer stringLocalizer)
        {
            _zanoConfiguration = zanoConfiguration;
            _storeRepository = storeRepository;
            _zanoRpcProvider = zanoRpcProvider;
            _handlers = handlers;
            StringLocalizer = stringLocalizer;
        }

        public StoreData StoreData => HttpContext.GetStoreData();

        private ZanoPaymentMethodViewModel GetZanoPaymentMethodViewModel(
            StoreData storeData, string cryptoCode,
            IPaymentFilter excludeFilters)
        {
            var zano = storeData.GetPaymentMethodConfigs(_handlers)
                .Where(s => s.Value is ZanoPaymentPromptDetails)
                .Select(s => (PaymentMethodId: s.Key, Details: (ZanoPaymentPromptDetails)s.Value));
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var settings = zano.Where(method => method.PaymentMethodId == pmi).Select(m => m.Details).SingleOrDefault();
            _zanoRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary);

            var settlementThresholdChoice = ZanoSettlementThresholdChoice.StoreSpeedPolicy;
            if (settings != null && settings.InvoiceSettledConfirmationThreshold is { } confirmations)
            {
                settlementThresholdChoice = confirmations switch
                {
                    0 => ZanoSettlementThresholdChoice.ZeroConfirmation,
                    1 => ZanoSettlementThresholdChoice.AtLeastOne,
                    10 => ZanoSettlementThresholdChoice.AtLeastTen,
                    _ => ZanoSettlementThresholdChoice.Custom
                };
            }

            return new ZanoPaymentMethodViewModel()
            {
                Enabled =
                    settings != null &&
                    !excludeFilters.Match(PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode)),
                Summary = summary,
                CryptoCode = cryptoCode,
                SettlementConfirmationThresholdChoice = settlementThresholdChoice,
                CustomSettlementConfirmationThreshold =
                    settings != null &&
                    settlementThresholdChoice is ZanoSettlementThresholdChoice.Custom
                        ? settings.InvoiceSettledConfirmationThreshold
                        : null
            };
        }

        [HttpGet("{cryptoCode}")]
        public IActionResult GetStoreZanoPaymentMethod(string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            if (!_zanoConfiguration.ZanoConfigurationItems.ContainsKey(cryptoCode))
            {
                return NotFound();
            }

            var vm = GetZanoPaymentMethodViewModel(StoreData, cryptoCode,
                StoreData.GetStoreBlob().GetExcludedPaymentMethods());
            return View("/Views/Zano/GetStoreZanoPaymentMethod.cshtml", vm);
        }

        [HttpPost("{cryptoCode}")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> GetStoreZanoPaymentMethod(ZanoPaymentMethodViewModel viewModel, string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            if (!_zanoConfiguration.ZanoConfigurationItems.TryGetValue(cryptoCode,
                out _))
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                var vm = GetZanoPaymentMethodViewModel(StoreData, cryptoCode,
                    StoreData.GetStoreBlob().GetExcludedPaymentMethods());
                vm.Enabled = viewModel.Enabled;
                vm.SettlementConfirmationThresholdChoice = viewModel.SettlementConfirmationThresholdChoice;
                vm.CustomSettlementConfirmationThreshold = viewModel.CustomSettlementConfirmationThreshold;
                return View("/Views/Zano/GetStoreZanoPaymentMethod.cshtml", vm);
            }

            var storeData = StoreData;
            var blob = storeData.GetStoreBlob();
            storeData.SetPaymentMethodConfig(_handlers[PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode)], new ZanoPaymentPromptDetails()
            {
                InvoiceSettledConfirmationThreshold = viewModel.SettlementConfirmationThresholdChoice switch
                {
                    ZanoSettlementThresholdChoice.ZeroConfirmation => 0,
                    ZanoSettlementThresholdChoice.AtLeastOne => 1,
                    ZanoSettlementThresholdChoice.AtLeastTen => 10,
                    ZanoSettlementThresholdChoice.Custom when viewModel.CustomSettlementConfirmationThreshold is { } custom => custom,
                    _ => null
                }
            });

            blob.SetExcluded(PaymentTypes.CHAIN.GetPaymentMethodId(viewModel.CryptoCode), !viewModel.Enabled);
            storeData.SetStoreBlob(blob);
            await _storeRepository.UpdateStore(storeData);
            return RedirectToAction("GetStoreZanoPaymentMethod", new { cryptoCode });
        }

        public class ZanoPaymentMethodViewModel : IValidatableObject
        {
            public ZanoRpcProvider.ZanoSummary Summary { get; set; }
            public string CryptoCode { get; set; }
            public bool Enabled { get; set; }

            [Display(Name = "Consider the invoice settled when the payment transaction ...")]
            public ZanoSettlementThresholdChoice SettlementConfirmationThresholdChoice { get; set; }
            [Display(Name = "Required Confirmations"), Range(0, 100)]
            public long? CustomSettlementConfirmationThreshold { get; set; }

            public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
            {
                if (SettlementConfirmationThresholdChoice is ZanoSettlementThresholdChoice.Custom
                    && CustomSettlementConfirmationThreshold is null)
                {
                    yield return new ValidationResult(
                        "You must specify the number of required confirmations when using a custom threshold.",
                        new[] { nameof(CustomSettlementConfirmationThreshold) });
                }
            }
        }

        public enum ZanoSettlementThresholdChoice
        {
            [Display(Name = "Store Speed Policy", Description = "Use the store's speed policy")]
            StoreSpeedPolicy,
            [Display(Name = "Zero Confirmation", Description = "Is unconfirmed")]
            ZeroConfirmation,
            [Display(Name = "At Least One", Description = "Has at least 1 confirmation")]
            AtLeastOne,
            [Display(Name = "At Least Ten", Description = "Has at least 10 confirmations")]
            AtLeastTen,
            [Display(Name = "Custom", Description = "Custom")]
            Custom
        }
    }
}
