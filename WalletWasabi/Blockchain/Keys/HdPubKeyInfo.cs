using NBitcoin;

namespace WalletWasabi.Blockchain.Keys;

public record HdPubKeyInfo(HdPubKey HdPubKey)
{
	public Script ScriptPubKey { get; } = HdPubKey.P2Taproot;
	public byte[] ScriptPubKeyBytes => ScriptPubKey.ToBytes(@unsafe: true);
}
