using BTCPayServer.Plugins.Zano.Services;
using BTCPayServer.Tests;

using Xunit;

namespace BTCPayServer.Plugins.IntegrationTests.Zano;

public static class IntegrationTestUtils
{
    public static async Task WaitForZanoAsync(PlaywrightTester tester)
    {
        var rpc = tester.Server.PayTester.GetService<ZanoRpcProvider>();
        await TestUtils.EventuallyAsync(async () =>
        {
            await rpc.UpdateSummary("ZANO");
            Assert.True(rpc.IsAvailable("ZANO"), "Zano daemon and pre-opened wallet must be synchronized.");
        }, delay: 120_000);
    }
}