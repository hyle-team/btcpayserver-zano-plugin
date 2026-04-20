using System;
using System.Collections.Generic;
using System.Globalization;

namespace BTCPayServer.Plugins.Zano;

public enum ZanoAssetRateMode
{
    None = 0,
    PeggedUsd,
    FixedUsd,
    CoinGecko
}

public class ZanoAssetDefinition
{
    public string Ticker { get; set; }
    public string AssetId { get; set; }
    public int Decimals { get; set; }
    public string DisplayName { get; set; }
    public ZanoAssetRateMode RateMode { get; set; } = ZanoAssetRateMode.None;
    public string RateParam { get; set; }

    // BTCPay's rate-rule parser treats currency identifiers as alphanumeric only —
    // hyphens cause InvalidCurrencyIdentifier at network registration. Keep the
    // crypto code concatenated; the ticker is still surfaced via AssetTicker/DisplayName.
    public string CryptoCode => $"ZANO{Ticker}";

    public string[] BuildDefaultRateRules()
    {
        return RateMode switch
        {
            ZanoAssetRateMode.None => Array.Empty<string>(),
            ZanoAssetRateMode.PeggedUsd => new[]
            {
                $"{CryptoCode}_X = USD_X"
            },
            ZanoAssetRateMode.FixedUsd => new[]
            {
                $"{CryptoCode}_USD = {RateParam}",
                $"{CryptoCode}_X = {CryptoCode}_USD * USD_X"
            },
            // coingecko ids frequently contain hyphens (e.g. "wrapped-bitcoin"), which
            // BTCPay's rate-rule parser rejects as InvalidCurrencyIdentifier. Until we
            // add a per-asset coingecko symbol mapping, fall back to no default rule —
            // the merchant can configure rates manually in BTCPay's UI.
            ZanoAssetRateMode.CoinGecko => Array.Empty<string>(),
            _ => Array.Empty<string>()
        };
    }

    public static IReadOnlyList<ZanoAssetDefinition> ParseExtraAssets(string raw)
    {
        var list = new List<ZanoAssetDefinition>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        var rows = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var row in rows)
        {
            var parts = row.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                throw new FormatException(
                    $"Invalid extra-asset entry '{row}'. Expected TICKER|asset_id|decimals[|DisplayName[|rate_mode[|rate_param]]].");
            }

            var ticker = parts[0].ToUpperInvariant();
            var assetId = parts[1].ToLowerInvariant();
            if (string.IsNullOrEmpty(ticker))
            {
                throw new FormatException("Extra-asset ticker is empty.");
            }
            if (string.Equals(ticker, ZanoAssets.NativeTicker, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"Extra-asset ticker '{ticker}' collides with native ZANO.");
            }
            if (assetId.Length != 64)
            {
                throw new FormatException(
                    $"Extra-asset '{ticker}' asset_id must be 64 hex chars (got {assetId.Length}).");
            }
            if (string.Equals(assetId, ZanoAssets.NativeAssetId, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException(
                    $"Extra-asset '{ticker}' uses the native ZANO asset_id; configure it via the built-in native registration instead.");
            }
            if (!int.TryParse(parts[2], out var decimals) || decimals < 0 || decimals > 18)
            {
                throw new FormatException(
                    $"Extra-asset '{ticker}' decimals must be an integer in [0, 18] (got '{parts[2]}').");
            }

            var displayName = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : ticker;
            var rateMode = ZanoAssetRateMode.None;
            string rateParam = null;

            if (parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[4]))
            {
                rateMode = ParseRateMode(ticker, parts[4]);
            }
            if (parts.Length >= 6 && !string.IsNullOrWhiteSpace(parts[5]))
            {
                rateParam = parts[5];
            }

            ValidateRateParam(ticker, rateMode, rateParam);

            list.Add(new ZanoAssetDefinition
            {
                Ticker = ticker,
                AssetId = assetId,
                Decimals = decimals,
                DisplayName = displayName,
                RateMode = rateMode,
                RateParam = rateParam
            });
        }

        var seenTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAssetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in list)
        {
            if (!seenTickers.Add(a.Ticker))
            {
                throw new FormatException($"Duplicate extra-asset ticker '{a.Ticker}'.");
            }
            if (!seenAssetIds.Add(a.AssetId))
            {
                throw new FormatException($"Duplicate extra-asset asset_id '{a.AssetId}'.");
            }
        }

        return list;
    }

    private static ZanoAssetRateMode ParseRateMode(string ticker, string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "none" or "" => ZanoAssetRateMode.None,
            "pegged_usd" or "pegged-usd" or "peggedusd" => ZanoAssetRateMode.PeggedUsd,
            "fixed_usd" or "fixed-usd" or "fixedusd" or "fixed" => ZanoAssetRateMode.FixedUsd,
            "coingecko" or "cg" => ZanoAssetRateMode.CoinGecko,
            _ => throw new FormatException(
                $"Extra-asset '{ticker}' has unknown rate_mode '{raw}'. Expected one of: none, pegged_usd, fixed_usd, coingecko.")
        };
    }

    private static void ValidateRateParam(string ticker, ZanoAssetRateMode mode, string rateParam)
    {
        switch (mode)
        {
            case ZanoAssetRateMode.None:
            case ZanoAssetRateMode.PeggedUsd:
                // rate_param ignored for these modes
                break;

            case ZanoAssetRateMode.FixedUsd:
                if (string.IsNullOrWhiteSpace(rateParam))
                {
                    throw new FormatException(
                        $"Extra-asset '{ticker}' rate_mode=fixed_usd requires a price in rate_param (e.g. '1.00').");
                }
                if (!decimal.TryParse(rateParam, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0m)
                {
                    throw new FormatException(
                        $"Extra-asset '{ticker}' rate_mode=fixed_usd rate_param '{rateParam}' must be a positive decimal.");
                }
                break;

            case ZanoAssetRateMode.CoinGecko:
                if (string.IsNullOrWhiteSpace(rateParam))
                {
                    throw new FormatException(
                        $"Extra-asset '{ticker}' rate_mode=coingecko requires a coingecko coin id in rate_param (e.g. 'wrapped-bitcoin').");
                }
                foreach (var ch in rateParam)
                {
                    if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                    {
                        throw new FormatException(
                            $"Extra-asset '{ticker}' coingecko id '{rateParam}' contains invalid characters (allowed: alphanumerics, '-', '_').");
                    }
                }
                break;
        }
    }
}
