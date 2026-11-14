using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;

namespace WalletWasabi.Fluent.Models.Transactions;

public record RecipientInfo(
	Destination Destination,
	Money Amount,
	bool IsSubtractFee = false);
