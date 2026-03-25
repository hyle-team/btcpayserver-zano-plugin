using BTCPayServer.Tests;

using Xunit.Abstractions;

namespace BTCPayServer.Plugins.IntegrationTests.Zano
{
    public class ZanoAndBitcoinIntegrationTestBase : UnitTestBase
    {

        public ZanoAndBitcoinIntegrationTestBase(ITestOutputHelper helper) : base(helper)
        {
            SetDefaultEnv("BTCPAY_ZANO_DAEMON_URI", "http://127.0.0.1:11211");
            SetDefaultEnv("BTCPAY_ZANO_WALLET_DAEMON_URI", "http://127.0.0.1:11212");
            SetDefaultEnv("BTCPAY_ZANO_WALLET_DAEMON_WALLETDIR", "/wallet");
        }

        private static void SetDefaultEnv(string key, string defaultValue)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, defaultValue);
            }
        }
    }
}