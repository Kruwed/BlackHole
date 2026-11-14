namespace WalletWasabi.Extensions;

public static class NetworkExtensions
{
	extension(Network network)
	{
		/// <summary>
		/// Height of the first block that can have a payment to the wallet: wallets are Taproot only, and this is the block that Taproot activated at.
		/// </summary>
		public ChainHeight TaprootActivationHeight =>
			network == Network.Main
				? new ChainHeight(709_632)
				: ChainHeight.Genesis;
	}
}
