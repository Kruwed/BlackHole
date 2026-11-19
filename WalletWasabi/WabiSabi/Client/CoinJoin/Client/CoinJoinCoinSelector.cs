using WalletWasabi.Crypto.Randomness;

namespace WalletWasabi.WabiSabi.Client.CoinJoin.Client;

/// <summary>
/// Tells which coins a wallet registers in a round: every coin that may take part and that the round takes. Which
/// coins may take part is the choice of the user, and the wallet does not choose among them.
/// </summary>
public static class CoinJoinCoinSelector
{
	/// <param name="coins">The coins that may take part: the ones that the user lets into coinjoins and that can be registered now.</param>
	/// <returns>The coins to register, in random order. None if the round has no use for the coins.</returns>
	public static ImmutableList<SmartCoin> SelectCoinsForRound(IEnumerable<SmartCoin> coins, UtxoSelectionParameters parameters)
	{
		var allowedCoins = coins
			.Where(parameters.Allows)
			.ToArray();

		if (allowedCoins.Length == 0)
		{
			Logger.LogDebug("The round takes none of the coins.");
			return [];
		}

		// A round has no output for less than its smallest amount, so coins that do not add up to it would only pay fees.
		var effectiveSum = Money.Satoshis(allowedCoins.Sum(x => x.EffectiveValue(parameters.MiningFeeRate)));
		if (effectiveSum < parameters.MinAllowedOutputAmount)
		{
			Logger.LogDebug($"The {allowedCoins.Length} coins that the round takes are not worth an output of it: {effectiveSum.ToString(false, true)} BTC after fees.");
			return [];
		}

		Logger.LogDebug($"{allowedCoins.Length} coins are to be registered, valued at {Money.Satoshis(allowedCoins.Sum(x => x.Amount)).ToString(false, true)} BTC.");

		// The coins are registered in this order, which is to tell nothing about them.
		return allowedCoins.ToShuffled(RandomnessProviders.Secure).ToImmutableList();
	}
}
