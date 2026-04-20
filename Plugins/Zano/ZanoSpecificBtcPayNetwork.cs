namespace BTCPayServer.Plugins.Zano;

public class ZanoSpecificBtcPayNetwork : BTCPayNetworkBase
{
    public int MaxTrackedConfirmation = 10;
    public string UriScheme { get; set; }
    public string AssetId { get; set; }
    public string AssetTicker { get; set; }
    public bool IsNative { get; set; }
}
