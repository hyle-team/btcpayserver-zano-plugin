using System.Globalization;

using BTCPayServer.Plugins.Zano.Utils;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Utils
{
    public class ZanoMoneyTests
    {
        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(1, "0.000000000001")]
        [InlineData(123456789012, "0.123456789012")]
        [InlineData(1000000000000, "1.000000000000")]
        public void Convert_LongToDecimal_ReturnsExpectedValue(long atomicUnits, string expectedString)
        {
            decimal expected = decimal.Parse(expectedString, CultureInfo.InvariantCulture);
            decimal result = ZanoMoney.Convert(atomicUnits);
            Assert.Equal(expected, result);
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData("0.000000000001", 1)]
        [InlineData("0.123456789012", 123456789012)]
        [InlineData("1.000000000000", 1000000000000)]
        public void Convert_DecimalToLong_ReturnsExpectedValue(string zanoString, long expectedAtomicUnits)
        {
            decimal zano = decimal.Parse(zanoString, CultureInfo.InvariantCulture);
            long result = ZanoMoney.Convert(zano);
            Assert.Equal(expectedAtomicUnits, result);
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(1)]
        [InlineData(123456789012)]
        [InlineData(1000000000000)]
        public void RoundTripConversion_LongToDecimalToLong_ReturnsOriginalValue(long atomicUnits)
        {
            decimal zano = ZanoMoney.Convert(atomicUnits);
            long convertedBack = ZanoMoney.Convert(zano);
            Assert.Equal(atomicUnits, convertedBack);
        }
    }
}