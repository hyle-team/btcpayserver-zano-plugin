using System;

using BTCPayServer.Plugins.Zano;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano
{
    public class ZanoAssetDefinitionTests
    {
        private const string ValidAssetId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string OtherAssetId = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(ZanoAssetDefinition.ParseExtraAssets(null));
            Assert.Empty(ZanoAssetDefinition.ParseExtraAssets(""));
            Assert.Empty(ZanoAssetDefinition.ParseExtraAssets("   "));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_SingleEntry_Parsed()
        {
            var result = ZanoAssetDefinition.ParseExtraAssets($"USDZ|{ValidAssetId}|6|USD Zano");
            var a = Assert.Single(result);
            Assert.Equal("USDZ", a.Ticker);
            Assert.Equal(ValidAssetId, a.AssetId);
            Assert.Equal(6, a.Decimals);
            Assert.Equal("USD Zano", a.DisplayName);
            Assert.Equal("ZANOUSDZ", a.CryptoCode);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_DisplayNameOptional_FallsBackToTicker()
        {
            var result = ZanoAssetDefinition.ParseExtraAssets($"USDZ|{ValidAssetId}|6");
            Assert.Equal("USDZ", result[0].DisplayName);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_MultipleEntries_Parsed()
        {
            var raw = $"USDZ|{ValidAssetId}|6|USD Zano;BTCZ|{OtherAssetId}|8";
            var result = ZanoAssetDefinition.ParseExtraAssets(raw);
            Assert.Equal(2, result.Count);
            Assert.Equal("USDZ", result[0].Ticker);
            Assert.Equal("BTCZ", result[1].Ticker);
            Assert.Equal(8, result[1].Decimals);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_TickerAndAssetIdNormalized()
        {
            var result = ZanoAssetDefinition.ParseExtraAssets($"usdz|{ValidAssetId.ToUpperInvariant()}|6");
            Assert.Equal("USDZ", result[0].Ticker);
            Assert.Equal(ValidAssetId, result[0].AssetId);
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData("ZANO|" + ValidAssetId + "|12")]  // collides with native ticker
        [InlineData("USDZ|tooshort|6")]                // bad asset_id length
        [InlineData("USDZ|" + ValidAssetId + "|-1")]   // negative decimals
        [InlineData("USDZ|" + ValidAssetId + "|99")]   // decimals out of range
        [InlineData("USDZ|" + ValidAssetId)]           // missing decimals
        [InlineData("|" + ValidAssetId + "|6")]        // empty ticker
        public void ParseExtraAssets_Invalid_Throws(string raw)
        {
            Assert.Throws<FormatException>(() => ZanoAssetDefinition.ParseExtraAssets(raw));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_NativeAssetIdRejected()
        {
            var raw = $"FOO|{ZanoAssets.NativeAssetId}|12";
            Assert.Throws<FormatException>(() => ZanoAssetDefinition.ParseExtraAssets(raw));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_DuplicateTicker_Throws()
        {
            var raw = $"USDZ|{ValidAssetId}|6;USDZ|{OtherAssetId}|6";
            Assert.Throws<FormatException>(() => ZanoAssetDefinition.ParseExtraAssets(raw));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_DuplicateAssetId_Throws()
        {
            var raw = $"USDZ|{ValidAssetId}|6;USDY|{ValidAssetId}|6";
            Assert.Throws<FormatException>(() => ZanoAssetDefinition.ParseExtraAssets(raw));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_DefaultRateMode_IsNone()
        {
            var a = ZanoAssetDefinition.ParseExtraAssets($"USDZ|{ValidAssetId}|6")[0];
            Assert.Equal(ZanoAssetRateMode.None, a.RateMode);
            Assert.Empty(a.BuildDefaultRateRules());
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void RateMode_PeggedUsd_BuildsRule()
        {
            var a = ZanoAssetDefinition.ParseExtraAssets($"USDZ|{ValidAssetId}|6|USD Zano|pegged_usd")[0];
            Assert.Equal(ZanoAssetRateMode.PeggedUsd, a.RateMode);
            var rules = a.BuildDefaultRateRules();
            Assert.Single(rules);
            Assert.Equal("ZANOUSDZ_X = USD_X", rules[0]);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void RateMode_FixedUsd_BuildsRules()
        {
            var a = ZanoAssetDefinition.ParseExtraAssets($"FOOZ|{ValidAssetId}|8||fixed_usd|0.50")[0];
            Assert.Equal(ZanoAssetRateMode.FixedUsd, a.RateMode);
            var rules = a.BuildDefaultRateRules();
            Assert.Equal(2, rules.Length);
            Assert.Equal("ZANOFOOZ_USD = 0.50", rules[0]);
            Assert.Equal("ZANOFOOZ_X = ZANOFOOZ_USD * USD_X", rules[1]);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void RateMode_CoinGecko_ParsesButEmitsNoRules()
        {
            // BTCPay's rate-rule parser rejects hyphenated coingecko ids, so for now
            // coingecko mode is accepted at parse time but produces no default rule —
            // merchants configure rates manually.
            var a = ZanoAssetDefinition.ParseExtraAssets($"BTCZ|{ValidAssetId}|12|Wrapped BTC|coingecko|wrapped-bitcoin")[0];
            Assert.Equal(ZanoAssetRateMode.CoinGecko, a.RateMode);
            Assert.Equal("wrapped-bitcoin", a.RateParam);
            Assert.Empty(a.BuildDefaultRateRules());
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData("pegged_usd")]
        [InlineData("PEGGED-USD")]
        [InlineData("peggedusd")]
        public void ParseExtraAssets_RateModeAliases_Accepted(string alias)
        {
            var a = ZanoAssetDefinition.ParseExtraAssets($"USDZ|{ValidAssetId}|6||{alias}")[0];
            Assert.Equal(ZanoAssetRateMode.PeggedUsd, a.RateMode);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ParseExtraAssets_UnknownRateMode_Throws()
        {
            Assert.Throws<FormatException>(() =>
                ZanoAssetDefinition.ParseExtraAssets($"USDZ|{ValidAssetId}|6||garbage"));
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData("fixed_usd", "")]        // missing param
        [InlineData("fixed_usd", "notanumber")]
        [InlineData("fixed_usd", "0")]       // not positive
        [InlineData("fixed_usd", "-1.5")]
        [InlineData("coingecko", "")]        // missing param
        [InlineData("coingecko", "has space")]
        [InlineData("coingecko", "bad!chars")]
        public void ParseExtraAssets_InvalidRateParam_Throws(string mode, string param)
        {
            Assert.Throws<FormatException>(() =>
                ZanoAssetDefinition.ParseExtraAssets($"USDZ|{ValidAssetId}|6||{mode}|{param}"));
        }
    }
}
