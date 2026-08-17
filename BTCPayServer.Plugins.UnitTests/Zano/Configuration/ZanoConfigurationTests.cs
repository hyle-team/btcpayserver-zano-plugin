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
        public void GroupByWallet_GroupsCryptosSharingAWalletUri()
        {
            var sharedWallet = new Uri("http://localhost:11212/");
            var config = new ZanoConfiguration();
            config.ZanoConfigurationItems.Add("ZANO", new ZanoConfigurationItem
            {
                DaemonRpcUri = new Uri("http://localhost:11211"),
                InternalWalletRpcUri = sharedWallet
            });
            config.ZanoConfigurationItems.Add("ZANOTEST", new ZanoConfigurationItem
            {
                DaemonRpcUri = new Uri("http://localhost:11211"),
                InternalWalletRpcUri = sharedWallet
            });
            config.ZanoConfigurationItems.Add("ZANOALT", new ZanoConfigurationItem
            {
                DaemonRpcUri = new Uri("http://localhost:11221"),
                InternalWalletRpcUri = new Uri("http://localhost:11222/")
            });

            var groups = config.GroupByWallet();

            Assert.Equal(2, groups.Count);
            var sharedGroup = Assert.Single(groups, g => g.CryptoCodes.Count == 2);
            Assert.Contains("ZANO", sharedGroup.CryptoCodes);
            Assert.Contains("ZANOTEST", sharedGroup.CryptoCodes);
            Assert.Equal("http://localhost:11212/", sharedGroup.WalletKey);

            var altGroup = Assert.Single(groups, g => g.CryptoCodes.Count == 1);
            Assert.Equal("ZANOALT", altGroup.CryptoCodes[0]);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void GroupByWallet_TreatsUriCaseInsensitively()
        {
            var config = new ZanoConfiguration();
            config.ZanoConfigurationItems.Add("ZANO", new ZanoConfigurationItem
            {
                InternalWalletRpcUri = new Uri("http://LocalHost:11212/")
            });
            config.ZanoConfigurationItems.Add("ZANOTEST", new ZanoConfigurationItem
            {
                InternalWalletRpcUri = new Uri("http://localhost:11212/")
            });

            var groups = config.GroupByWallet();

            var group = Assert.Single(groups);
            Assert.Equal(2, group.CryptoCodes.Count);
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
