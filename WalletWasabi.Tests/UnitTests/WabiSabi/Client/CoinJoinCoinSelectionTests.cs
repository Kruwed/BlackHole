using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

/// <summary>
/// Tests for <see cref="CoinJoinCoinSelector"/>.
/// </summary>
public class CoinJoinCoinSelectionTests
{
	/// <summary>
	/// This test is to make sure no coins are selected when there are no coins.
	/// </summary>
	[Fact]
	public void SelectNothingFromEmptySetOfCoins()
	{
		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: 10, semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: [],
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Empty(coins);
	}

	/// <summary>
	/// This test is to make sure no coins are selected when all coins are private.
	/// </summary>
	[Fact]
	public void SelectNothingFromFullyPrivateSetOfCoins()
	{
		const int AnonymitySet = 10;
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Range(0, 10)
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: AnonymitySet + 1))
			.ToList();

		// Make sure the distance from external keys is sufficient.
		foreach (var sc in coinsToSelectFrom)
		{
			var sci = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: AnonymitySet + 1);
			sci.Transaction.TryAddWalletInput(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: AnonymitySet + 1));
			sc.Transaction.TryAddWalletInput(sci);
		}

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);
		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: AnonymitySet, semiPrivateThreshold: 0, generator);

		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Empty(coins);
	}

	[Fact]
	public void SelectNothingFromTooSmallCoin()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = new[] { BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00017423m), anonymitySet: 1) };
		var roundParams = WabiSabiFactory.CreateRoundParameters(new()
		{
			MinRegistrableAmount = Money.Coins(0.0001m),
			MaxRegistrableAmount = Money.Coins(430),
		});

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: 10, semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			UtxoSelectionParameters.FromRoundParameters(roundParams, [ScriptType.Taproot]),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Empty(coins);
	}

	/// <summary>
	/// This test is to make sure no coins are selected when there too small coins.
	/// </summary>
	[Fact]
	public void SelectNothingFromTooSmallSetOfCoins()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = new[]
		{
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008711m + 0.00005800m), anonymitySet: 1),
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008710m + 0.00005800m), anonymitySet: 1)
		};
		var roundParams = WabiSabiFactory.CreateRoundParameters(new()
		{
			MinRegistrableAmount = Money.Coins(0.0001m),
			MaxRegistrableAmount = Money.Coins(430),
		});

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: 10, semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			UtxoSelectionParameters.FromRoundParameters(roundParams, [ScriptType.Taproot]),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Empty(coins);
	}

	/// <summary>
	/// This test is to make sure the coins are selected when the selection's effective sum is exactly the smallest reasonable effective denom.
	/// </summary>
	[Fact]
	public void SelectSomethingFromJustEnoughSetOfCoins()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = new[]
		{
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008711m + 0.00005800m), anonymitySet: 1),
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008711m + 0.00005800m), anonymitySet: 1)
		};
		var roundParams = WabiSabiFactory.CreateRoundParameters(new()
		{
			MinRegistrableAmount = Money.Coins(0.0001m),
			MaxRegistrableAmount = Money.Coins(430),
		});

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: 10, semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			UtxoSelectionParameters.FromRoundParameters(roundParams, [ScriptType.Taproot]),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.NotEmpty(coins);
	}

	/// <summary>
	/// This test is to make sure that we select the only non-private coin when it is the only coin in the wallet.
	/// </summary>
	[Fact]
	public void SelectNonPrivateCoinFromOneCoinSetOfCoins()
	{
		const int AnonymitySet = 10;
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Empty<SmartCoin>()
			.Prepend(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m), anonymitySet: AnonymitySet - 1))
			.ToList();

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 10);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: AnonymitySet, semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Single(coins);
	}

	/// <summary>
	/// This test is to make sure that we select more non-private coins when they are coming from different txs.
	/// </summary>
	/// <remarks>Note randomization can make this test fail even though that's unlikely.</remarks>
	[Fact]
	public void SelectMoreNonPrivateCoinFromTwoCoinsSetOfCoins()
	{
		const int AnonymitySet = 10;
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Empty<SmartCoin>()
			.Prepend(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m), anonymitySet: AnonymitySet - 1))
			.Prepend(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m), anonymitySet: AnonymitySet - 1))
			.ToList();

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 10, sameTxAllowance: 0);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: AnonymitySet, semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Equal(2, coins.Count);
	}

	/// <summary>
	/// This test is to make sure no coins are selected when all coins are private.
	/// </summary>
	[Fact]
	public void SelectNothingFromFullyPrivateAndBelowMinAllowedSetOfCoins()
	{
		const int AnonymitySet = 10;
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Range(0, 10)
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: AnonymitySet + 1))
			.ToList();

		var utxoSelectionParameter = WabiSabiFactory.CreateUtxoSelectionParameters();
		coinsToSelectFrom.Add(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), utxoSelectionParameter.AllowedInputAmounts.Min - Money.Satoshis(1), anonymitySet: AnonymitySet - 1));

		// Make sure the distance from external keys is sufficient.
		foreach (var sc in coinsToSelectFrom)
		{
			var sci = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: AnonymitySet + 1);
			sci.Transaction.TryAddWalletInput(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: AnonymitySet + 1));
			sc.Transaction.TryAddWalletInput(sci);
		}

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);
		var coinJoinCoinSelector = new CoinJoinCoinSelector(anonScoreTarget: AnonymitySet, semiPrivateThreshold: 0, generator);

		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			utxoSelectionParameter,
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Empty(coins);
	}

	private static CoinJoinCoinSelectorRandomnessGenerator CreateSelectorGenerator(int inputTarget, int? sameTxAllowance = null)
	{
		GetInputTargetSelector fixedInputTarget = () => inputTarget;
		GetSameTxAllowanceSelector? fixedSameTxAllowance = sameTxAllowance is not null
				? (percent) => sameTxAllowance.Value
				: null;

		var generator = new CoinJoinCoinSelectorRandomnessGenerator(
			CoinJoinCoinSelector.MaxInputsRegistrableByWallet,
			RandomnessProviders.Insecure,
			fixedInputTarget,
			fixedSameTxAllowance);

		return generator;
	}
}
