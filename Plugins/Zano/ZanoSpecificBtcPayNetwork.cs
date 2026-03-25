namespace BTCPayServer.Plugins.Zano;

public class ZanoSpecificBtcPayNetwork : BTCPayNetworkBase
{
    public int MaxTrackedConfirmation = 10;
    public string UriScheme { get; set; }
}