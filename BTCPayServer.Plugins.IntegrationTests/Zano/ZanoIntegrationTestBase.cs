using BTCPayServer.Tests;

using Xunit;

namespace BTCPayServer.Plugins.IntegrationTests.Zano
{
    public class ZanoIntegrationTestBase : UnitTestBase
    {

        public ZanoIntegrationTestBase(ITestOutputHelper helper) : base(helper)
        {
            SetDefaultEnv("BTCPAY_ZANO_DAEMON_URI", "http://127.0.0.1:11211");
            SetDefaultEnv("BTCPAY_ZANO_WALLET_DAEMON_URI", "http://127.0.0.1:11212");
            SetDefaultEnv("BTCPAY_NODEFAULTCHAIN", "true");
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