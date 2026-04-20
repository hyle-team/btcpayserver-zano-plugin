using System.Globalization;

using BTCPayServer.Plugins.Zano.Utils;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Utils
{
    public class ZanoMoneyTests
    {
        private const int ZanoDecimals = 12;

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(1, "0.000000000001")]
        [InlineData(123456789012, "0.123456789012")]
        [InlineData(1000000000000, "1.000000000000")]
        public void Convert_LongToDecimal_ReturnsExpectedValue(long atomicUnits, string expectedString)
        {
            decimal expected = decimal.Parse(expectedString, CultureInfo.InvariantCulture);
            decimal result = ZanoMoney.Convert(atomicUnits, ZanoDecimals);
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
            long result = ZanoMoney.Convert(zano, ZanoDecimals);
            Assert.Equal(expectedAtomicUnits, result);
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(1)]
        [InlineData(123456789012)]
        [InlineData(1000000000000)]
        public void RoundTripConversion_LongToDecimalToLong_ReturnsOriginalValue(long atomicUnits)
        {
            decimal zano = ZanoMoney.Convert(atomicUnits, ZanoDecimals);
            long convertedBack = ZanoMoney.Convert(zano, ZanoDecimals);
            Assert.Equal(atomicUnits, convertedBack);
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(1, 6, "0.000001")]
        [InlineData(1000000, 6, "1.000000")]
        [InlineData(100, 0, "100")]
        [InlineData(5, 18, "0.000000000000000005")]
        public void Convert_LongToDecimal_CustomDecimals(long atomicUnits, int decimals, string expectedString)
        {
            decimal expected = decimal.Parse(expectedString, CultureInfo.InvariantCulture);
            decimal result = ZanoMoney.Convert(atomicUnits, decimals);
            Assert.Equal(expected, result);
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData("0.000001", 6, 1)]
        [InlineData("1.000000", 6, 1000000)]
        [InlineData("100", 0, 100)]
        public void Convert_DecimalToLong_CustomDecimals(string input, int decimals, long expected)
        {
            decimal amt = decimal.Parse(input, CultureInfo.InvariantCulture);
            long result = ZanoMoney.Convert(amt, decimals);
            Assert.Equal(expected, result);
        }
    }
}