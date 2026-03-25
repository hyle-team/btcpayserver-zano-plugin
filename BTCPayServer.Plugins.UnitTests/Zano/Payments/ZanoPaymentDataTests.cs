using BTCPayServer.Plugins.Zano.Payments;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Payments
{
    public class ZanoPaymentDataTests
    {
        [Trait("Category", "Unit")]
        [Fact]
        public void DefaultValues_ShouldBeCorrect()
        {
            var paymentData = new ZanoPaymentData();

            Assert.Null(paymentData.PaymentId);
            Assert.Equal(0, paymentData.BlockHeight);
            Assert.Equal(0, paymentData.ConfirmationCount);
            Assert.Null(paymentData.TransactionId);
            Assert.Null(paymentData.InvoiceSettledConfirmationThreshold);
            Assert.Equal(0, paymentData.LockTime);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void Properties_ShouldBeSettable()
        {
            var paymentData = new ZanoPaymentData();

            paymentData.PaymentId = "abcdef0123456789";
            paymentData.BlockHeight = 100;
            paymentData.ConfirmationCount = 5;
            paymentData.TransactionId = "tx123";
            paymentData.InvoiceSettledConfirmationThreshold = 10;
            paymentData.LockTime = 50;

            Assert.Equal("abcdef0123456789", paymentData.PaymentId);
            Assert.Equal(100, paymentData.BlockHeight);
            Assert.Equal(5, paymentData.ConfirmationCount);
            Assert.Equal("tx123", paymentData.TransactionId);
            Assert.Equal(10, paymentData.InvoiceSettledConfirmationThreshold);
            Assert.Equal(50, paymentData.LockTime);
        }
    }
}
