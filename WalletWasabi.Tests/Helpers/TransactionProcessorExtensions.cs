using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionProcessing;

namespace WalletWasabi.Tests.Helpers;

public static class TransactionProcessorExtensions
{
	public static HdPubKey NewKey(this TransactionProcessor me)
	{
		return me.KeyManager.GenerateNewKey(KeyState.Clean, isInternal: true);
	}
}
