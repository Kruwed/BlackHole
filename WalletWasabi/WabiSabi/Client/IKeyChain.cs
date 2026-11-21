namespace WalletWasabi.WabiSabi.Client;

public interface IKeyChain
{
	OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData committedData);

	/// <param name="sigHash">What a Taproot input is signed with. Named, as it is by default here, it adds a byte to the signature.</param>
	Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin, TaprootSigHash sigHash = TaprootSigHash.All);
}
