using System;

using BTCPayServer.Plugins.Zano.Configuration;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Configuration
{
    public class ZanoConfigurationTests
    {
        [Trait("Category", "Unit")]
        [Fact]
        public void ZanoConfiguration_ShouldInitializeWithEmptyDictionary()
        {
            var config = new ZanoConfiguration();

            Assert.NotNull(config.ZanoConfigurationItems);
            Assert.Empty(config.ZanoConfigurationItems);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ZanoConfigurationItem_ShouldSetAndGetProperties()
        {
            var configItem = new ZanoConfigurationItem
            {
                DaemonRpcUri = new Uri("http://localhost:11211"),
                InternalWalletRpcUri = new Uri("http://localhost:11212"),
                WalletDirectory = "/wallets"
            };

            Assert.Equal("http://localhost:11211/", configItem.DaemonRpcUri.ToString());
            Assert.Equal("http://localhost:11212/", configItem.InternalWalletRpcUri.ToString());
            Assert.Equal("/wallets", configItem.WalletDirectory);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ZanoConfiguration_ShouldAddAndRetrieveItems()
        {
            var config = new ZanoConfiguration();
            var configItem = new ZanoConfigurationItem
            {
                DaemonRpcUri = new Uri("http://localhost:11211"),
                InternalWalletRpcUri = new Uri("http://localhost:11212"),
                WalletDirectory = "/wallets"
            };

            config.ZanoConfigurationItems.Add("ZANO", configItem);

            Assert.Single(config.ZanoConfigurationItems);
            Assert.True(config.ZanoConfigurationItems.ContainsKey("ZANO"));
            Assert.Equal(configItem, config.ZanoConfigurationItems["ZANO"]);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ZanoConfiguration_ShouldHandleDuplicateKeys()
        {
            var config = new ZanoConfiguration();
            var configItem1 = new ZanoConfigurationItem
            {
                DaemonRpcUri = new Uri("http://localhost:11211")
            };
            var configItem2 = new ZanoConfigurationItem
            {
                DaemonRpcUri = new Uri("http://localhost:11212")
            };

            config.ZanoConfigurationItems.Add("ZANO", configItem1);

            Assert.Throws<ArgumentException>(() =>
                config.ZanoConfigurationItems.Add("ZANO", configItem2));
        }
    }
}
