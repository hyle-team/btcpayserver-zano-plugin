using System.Globalization;

using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Zano.Validation;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Validation;

public class InvoiceAmountValidationTests
{
    [Theory]
    [InlineData("-5", false)]
    [InlineData("-0.000000000001", false)]
    [InlineData("0", true)]
    [InlineData("0.000000000001", true)]
    [InlineData("79228162514264337593543950335", true)]
    [InlineData(null, true)]
    public void ValidatesInvoiceAmountBeforeCreation(string? amount, bool valid)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        services.Configure<MvcOptions>(options =>
            options.ModelMetadataDetailsProviders.Add(new InvoiceAmountValidationMetadataProvider()));
        using var provider = services.BuildServiceProvider();
        var context = new ActionContext(new DefaultHttpContext { RequestServices = provider },
            new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var model = new CreateInvoiceModel
        {
            StoreId = "test-store",
            Amount = amount is null ? null : decimal.Parse(amount, CultureInfo.InvariantCulture)
        };

        provider.GetRequiredService<IObjectModelValidator>().Validate(context, null, string.Empty, model);

        Assert.Equal(valid, context.ModelState.IsValid);
        if (!valid)
        {
            Assert.Equal("The invoice amount cannot be negative.",
                Assert.Single(context.ModelState[nameof(CreateInvoiceModel.Amount)]!.Errors).ErrorMessage);
        }
    }
}