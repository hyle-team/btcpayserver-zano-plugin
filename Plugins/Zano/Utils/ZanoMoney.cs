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

        // Wide-amount overload (distinct name because C# can't overload on return type):
        // accepts atomic-unit amounts above long.MaxValue, which is required for
        // high-divisibility Confidential Assets (an 18-decimal CA tops out at
        // ~9.22 units when amounts are constrained to a signed 64-bit int).
        public static decimal FromAtomic(decimal atomicUnits, int decimals)
        {
            if (decimals < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(decimals));
            }
            if (decimals == 0)
            {
                return atomicUnits;
            }

            decimal divisor = 1m;
            for (var i = 0; i < decimals; i++)
            {
                divisor *= 10m;
            }
            return atomicUnits / divisor;
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