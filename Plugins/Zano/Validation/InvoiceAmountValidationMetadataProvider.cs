using System.ComponentModel.DataAnnotations;
using System.Globalization;

using BTCPayServer.Models.InvoicingModels;

using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace BTCPayServer.Plugins.Zano.Validation;

public sealed class InvoiceAmountValidationMetadataProvider : IValidationMetadataProvider
{
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        if (context.Key.ContainerType == typeof(CreateInvoiceModel)
            && context.Key.Name == nameof(CreateInvoiceModel.Amount))
        {
            context.ValidationMetadata.ValidatorMetadata.Add(new RangeAttribute(
                typeof(decimal), "0", decimal.MaxValue.ToString(CultureInfo.InvariantCulture))
            {
                ErrorMessage = "The invoice amount cannot be negative."
            });
        }
    }
}