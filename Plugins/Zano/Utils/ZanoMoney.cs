using System.Globalization;

namespace BTCPayServer.Plugins.Zano.Utils
{
    public static class ZanoMoney
    {
        public static decimal Convert(long atomicUnits)
        {
            var amt = atomicUnits.ToString(CultureInfo.InvariantCulture).PadLeft(12, '0');
            amt = amt.Length == 12 ? $"0.{amt}" : amt.Insert(amt.Length - 12, ".");

            return decimal.Parse(amt, CultureInfo.InvariantCulture);
        }

        public static long Convert(decimal zano)
        {
            return System.Convert.ToInt64(zano * 1000000000000);
        }
    }
}