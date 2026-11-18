using NBitcoin;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

public interface IWalletViewModel
{
	/// <summary>Marks the transaction in the history.</summary>
	/// <param name="showHistory">Whether the history is brought up as well. It is not when the user did not ask for the transaction.</param>
	void SelectTransaction(uint256 txid, bool showHistory);
}
