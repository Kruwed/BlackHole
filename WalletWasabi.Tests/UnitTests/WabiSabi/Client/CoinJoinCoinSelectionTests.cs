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

		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: [],
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Empty(coins);
	}

	/// <summary>
	/// No anonymity score keeps a coin out of a round: the user decides which coins take part, not a target that they
	/// have reached. With such a target, a wallet whose coins were all above it registered nothing.
	/// </summary>
	[Fact]
	public void SelectsCoinsWhateverTheirAnonymityScore()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Range(0, 10)
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: 300 + i))
			.ToList();

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);
		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);

		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		// As many coins as the selection aims for, each of them once.
		Assert.Equal(5, coins.Count);
		Assert.Equal(5, coins.Distinct().Count());
		Assert.All(coins, coin => Assert.Contains(coin, coinsToSelectFrom));
	}

	/// <summary>
	/// With the isolation of non-private coins, a round gets one of the coins that were never in a coinjoin. The coins
	/// that were are not held back by it, however high their anonymity score is.
	/// </summary>
	[Fact]
	public void SelectsOneNonPrivateCoinWhenTheyAreIsolated()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var neverCoinjoined = Enumerable
			.Range(0, 3)
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m), anonymitySet: 1))
			.ToArray();
		var coinjoined = new[]
		{
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: 2),
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: 50)
		};

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 10);
		var coinJoinCoinSelector = new CoinJoinCoinSelector(Constants.SemiPrivateThreshold, generator);

		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: neverCoinjoined.Concat(coinjoined),
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Equal(3, coins.Count);
		Assert.Single(coins, coin => neverCoinjoined.Contains(coin));
		Assert.All(coinjoined, coin => Assert.Contains(coin, coins));

		// Without the isolation every coin can be registered.
		var withoutIsolation = new CoinJoinCoinSelector(semiPrivateThreshold: 0, CreateSelectorGenerator(inputTarget: 10))
			.SelectCoinsForRound(neverCoinjoined.Concat(coinjoined), WabiSabiFactory.CreateUtxoSelectionParameters(), Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Equal(5, withoutIsolation.Count);
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

		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);
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

		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);
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

		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			UtxoSelectionParameters.FromRoundParameters(roundParams, [ScriptType.Taproot]),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.NotEmpty(coins);
	}

	/// <summary>
	/// This test is to make sure that we select the only coin when it is the only coin in the wallet.
	/// </summary>
	[Fact]
	public void SelectTheCoinFromOneCoinSetOfCoins()
	{
		const int AnonymitySet = 10;
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Empty<SmartCoin>()
			.Prepend(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m), anonymitySet: AnonymitySet - 1))
			.ToList();

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 10);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Single(coins);
	}

	/// <summary>
	/// This test is to make sure that we select more coins when they are coming from different txs.
	/// </summary>
	/// <remarks>Note randomization can make this test fail even though that's unlikely.</remarks>
	[Fact]
	public void SelectMoreCoinsFromTwoCoinsSetOfCoins()
	{
		const int AnonymitySet = 10;
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Empty<SmartCoin>()
			.Prepend(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m), anonymitySet: AnonymitySet - 1))
			.Prepend(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m), anonymitySet: AnonymitySet - 1))
			.ToList();

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 10, sameTxAllowance: 0);

		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);
		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			WabiSabiFactory.CreateUtxoSelectionParameters(),
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Equal(2, coins.Count);
	}

	/// <summary>
	/// A coin that is below the smallest amount of the round stays out of it. The other coins take part without it,
	/// whatever their anonymity scores are.
	/// </summary>
	[Fact]
	public void LeavesOutTheCoinThatIsBelowTheMinimumOfTheRound()
	{
		const int AnonymitySet = 10;
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Range(0, 10)
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), Money.Coins(1m), anonymitySet: AnonymitySet + 1))
			.ToList();

		var utxoSelectionParameter = WabiSabiFactory.CreateUtxoSelectionParameters();
		var tooSmall = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km, isInternal: true), utxoSelectionParameter.AllowedInputAmounts.Min - Money.Satoshis(1), anonymitySet: AnonymitySet - 1);
		coinsToSelectFrom.Add(tooSmall);

		CoinJoinCoinSelectorRandomnessGenerator generator = CreateSelectorGenerator(inputTarget: 5);
		var coinJoinCoinSelector = new CoinJoinCoinSelector(semiPrivateThreshold: 0, generator);

		var coins = coinJoinCoinSelector.SelectCoinsForRound(
			coins: coinsToSelectFrom,
			utxoSelectionParameter,
			liquidityClue: Constants.MaximumNumberOfBitcoinsMoney);

		Assert.Equal(5, coins.Count);
		Assert.DoesNotContain(tooSmall, coins);
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
