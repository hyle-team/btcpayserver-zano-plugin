using System;
using System.Globalization;

namespace BTCPayServer.Plugins.Zano.Utils
{
    public static class ZanoMoney
    {
        public static decimal Convert(long atomicUnits, int decimals)
        {
            if (decimals < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(decimals));
            }
            if (decimals == 0)
            {
                return atomicUnits;
            }

            var amt = atomicUnits.ToString(CultureInfo.InvariantCulture).PadLeft(decimals, '0');
            amt = amt.Length == decimals ? $"0.{amt}" : amt.Insert(amt.Length - decimals, ".");

            return decimal.Parse(amt, CultureInfo.InvariantCulture);
        }

        public static long Convert(decimal zano, int decimals)
        {
            if (decimals < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(decimals));
            }
            if (decimals == 0)
            {
                return System.Convert.ToInt64(zano);
            }

            var multiplier = 1L;
            for (var i = 0; i < decimals; i++)
            {
                multiplier *= 10;
            }
            return System.Convert.ToInt64(zano * multiplier);
        }
    }
}
