using BTCPayServer.Plugins.Zano.RPC;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.RPC
{
    public class ZanoPollEventTest
    {
        [Fact]
        public void DefaultInitialization_ShouldHaveNullCryptoCode()
        {
            var pollEvent = new ZanoPollEvent();

            Assert.Null(pollEvent.CryptoCode);
        }

        [Fact]
        public void PropertyAssignment_ShouldSetAndRetrieveValues()
        {
            var pollEvent = new ZanoPollEvent
            {
                CryptoCode = "ZANO"
            };

            Assert.Equal("ZANO", pollEvent.CryptoCode);
        }
    }
}
