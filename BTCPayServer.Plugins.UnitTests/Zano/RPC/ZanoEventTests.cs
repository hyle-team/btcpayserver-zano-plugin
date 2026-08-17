using BTCPayServer.Plugins.Zano.RPC;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.RPC
{
    public class ZanoWalletPollEventTest
    {
        [Fact]
        public void DefaultInitialization_ShouldHaveNullFields()
        {
            var pollEvent = new ZanoWalletPollEvent();

            Assert.Null(pollEvent.WalletKey);
            Assert.Null(pollEvent.CryptoCodes);
        }

        [Fact]
        public void PropertyAssignment_ShouldSetAndRetrieveValues()
        {
            var pollEvent = new ZanoWalletPollEvent
            {
                WalletKey = "http://wallet:12233/",
                CryptoCodes = new[] { "ZANO", "ZANOTEST" }
            };

            Assert.Equal("http://wallet:12233/", pollEvent.WalletKey);
            Assert.Equal(new[] { "ZANO", "ZANOTEST" }, pollEvent.CryptoCodes);
        }
    }
}