using WalletWasabi.Crypto.Randomness;

namespace WalletWasabi.WabiSabi.Client.CoinJoin.Client;

/// <summary>
/// Tells which coins a wallet registers in a round: the coins that may take part and that the round takes, but not
/// more than <see cref="MaxInputsRegistrableByWallet"/> of them. Which coins may take part is the choice of the user.
/// </summary>
public static class CoinJoinCoinSelector
{
	/// <summary>The most coins that a wallet registers in one round.</summary>
	public const int MaxInputsRegistrableByWallet = 10;

	/// <param name="coins">The coins that may take part: the ones that the user chose for coinjoins and that can be registered now.</param>
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

		// If the user chose more coins than a round gets, the largest go first and the others wait for a later round.
		// Nothing but the amount decides: coins of the same amount are taken in no order.
		var coinsToRegister = allowedCoins
			.ToShuffled(RandomnessProviders.Secure)
			.OrderByDescending(x => x.Amount)
			.Take(MaxInputsRegistrableByWallet)
			.ToArray();

		// A round has no output for less than its smallest amount, so coins that do not add up to it would only pay fees.
		var effectiveSum = Money.Satoshis(coinsToRegister.Sum(x => x.EffectiveValue(parameters.MiningFeeRate)));
		if (effectiveSum < parameters.MinAllowedOutputAmount)
		{
			Logger.LogDebug($"The {coinsToRegister.Length} coins for the round are not worth an output of it: {effectiveSum.ToString(false, true)} BTC after fees.");
			return [];
		}

		Logger.LogDebug($"{coinsToRegister.Length} of {allowedCoins.Length} coins are to be registered, valued at {Money.Satoshis(coinsToRegister.Sum(x => x.Amount)).ToString(false, true)} BTC.");

		// The coins are registered in this order, which is to tell nothing about them.
		return coinsToRegister.ToShuffled(RandomnessProviders.Secure).ToImmutableList();
	}
}
