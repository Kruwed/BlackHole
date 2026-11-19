using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

/// <summary>
/// Tests for <see cref="CoinJoinCoinSelector"/>: a round gets the coins that the user chose for coinjoins and that it
/// takes, ten of them at most.
/// </summary>
public class CoinJoinCoinSelectionTests
{
	[Fact]
	public void SelectNothingFromEmptySetOfCoins()
	{
		var coins = CoinJoinCoinSelector.SelectCoinsForRound(coins: [], WabiSabiFactory.CreateUtxoSelectionParameters());

		Assert.Empty(coins);
	}

	/// <summary>
	/// Up to ten coins, the wallet does not choose among them: each of them is registered.
	/// </summary>
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(9)]
	[InlineData(10)]
	public void SelectsEveryCoinUpToTen(int coinCount)
	{
		var coinsToSelectFrom = CreateCoinsOfDifferentAmounts(coinCount);

		var coins = CoinJoinCoinSelector.SelectCoinsForRound(coinsToSelectFrom, WabiSabiFactory.CreateUtxoSelectionParameters());

		AssertSameCoins(coinsToSelectFrom, coins);
	}

	/// <summary>
	/// A round gets ten coins of a wallet at most. Of more coins, the ten largest are registered.
	/// </summary>
	[Theory]
	[InlineData(11)]
	[InlineData(12)]
	[InlineData(40)]
	public void SelectsTheTenLargestOfMoreCoins(int coinCount)
	{
		var coinsToSelectFrom = CreateCoinsOfDifferentAmounts(coinCount);

		var coins = CoinJoinCoinSelector.SelectCoinsForRound(coinsToSelectFrom, WabiSabiFactory.CreateUtxoSelectionParameters());

		Assert.Equal(CoinJoinCoinSelector.MaxInputsRegistrableByWallet, coins.Count);
		AssertSameCoins(coinsToSelectFrom.OrderByDescending(x => x.Amount).Take(10).ToList(), coins);
	}

	/// <summary>
	/// The coins that a round did not get are still there for the next one.
	/// </summary>
	[Fact]
	public void CoinsThatWereLeftGoIntoALaterRound()
	{
		var coinsToSelectFrom = CreateCoinsOfDifferentAmounts(14);

		var firstRound = CoinJoinCoinSelector.SelectCoinsForRound(coinsToSelectFrom, WabiSabiFactory.CreateUtxoSelectionParameters());
		var left = coinsToSelectFrom.Except(firstRound).ToList();
		var secondRound = CoinJoinCoinSelector.SelectCoinsForRound(left, WabiSabiFactory.CreateUtxoSelectionParameters());

		Assert.Equal(10, firstRound.Count);
		Assert.Equal(4, left.Count);
		AssertSameCoins(left, secondRound);
		Assert.True(left.Max(x => x.Amount) < firstRound.Min(x => x.Amount));
	}

	/// <summary>
	/// Nothing but the amount decides which coins a round gets: of coins of the same amount, any may be the ones.
	/// </summary>
	[Fact]
	public void CoinsOfTheSameAmountAreTakenInNoOrder()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var larger = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(2m));
		var equal = Enumerable
			.Range(0, 12)
			.Select(_ => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m)))
			.ToList();

		var selections = Enumerable
			.Range(0, 20)
			.Select(_ => CoinJoinCoinSelector.SelectCoinsForRound([.. equal, larger], WabiSabiFactory.CreateUtxoSelectionParameters()))
			.ToList();

		Assert.All(selections, selection => Assert.Equal(10, selection.Count));
		Assert.All(selections, selection => Assert.Contains(larger, selection));

		// Twelve coins give 220 ways to take nine of them: twenty selections that are all the same do not happen by chance.
		Assert.True(selections.Select(selection => string.Join(",", selection.Select(x => x.Outpoint.ToString()).Order())).Distinct().Count() > 1);
	}

	/// <summary>
	/// Coins that one transaction made are registered together like any other coins. Whether to bring them into one
	/// round is up to the user.
	/// </summary>
	[Fact]
	public void SelectsTheCoinsOfOneTransactionTogether()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var keys = Enumerable.Range(0, 5).Select(_ => BitcoinFactory.CreateHdPubKey(km)).ToArray();

		var tx = Transaction.Create(Network.Main);
		tx.Inputs.Add(BitcoinFactory.CreateOutPoint());
		foreach (var key in keys)
		{
			tx.Outputs.Add(new TxOut(Money.Coins(1m), key.P2Taproot));
		}

		var transaction = new SmartTransaction(tx, new Height.ChainHeight(100));
		var coinsOfTheTransaction = keys.Select((key, i) => new SmartCoin(transaction, (uint)i, key)).ToArray();
		var anotherCoin = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m));

		var coins = CoinJoinCoinSelector.SelectCoinsForRound([.. coinsOfTheTransaction, anotherCoin], WabiSabiFactory.CreateUtxoSelectionParameters());

		AssertSameCoins([.. coinsOfTheTransaction, anotherCoin], coins);
	}

	/// <summary>
	/// A coin that is below the smallest or above the largest amount of the round stays out of it. The other coins take
	/// part without it, and it does not use up one of their ten places.
	/// </summary>
	[Fact]
	public void LeavesOutTheCoinsThatTheRoundDoesNotTake()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var parameters = WabiSabiFactory.CreateUtxoSelectionParameters();
		var taken = Enumerable
			.Range(0, 10)
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(1m)))
			.ToList();
		var tooSmall = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), parameters.AllowedInputAmounts.Min - Money.Satoshis(1));
		var tooLarge = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), parameters.AllowedInputAmounts.Max + Money.Satoshis(1));

		var coins = CoinJoinCoinSelector.SelectCoinsForRound([tooSmall, .. taken, tooLarge], parameters);

		AssertSameCoins(taken, coins);
	}

	[Fact]
	public void SelectNothingFromTooSmallCoin()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = new[] { BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00017423m)) };
		var roundParams = WabiSabiFactory.CreateRoundParameters(new()
		{
			MinRegistrableAmount = Money.Coins(0.0001m),
			MaxRegistrableAmount = Money.Coins(430),
		});

		var coins = CoinJoinCoinSelector.SelectCoinsForRound(coinsToSelectFrom, UtxoSelectionParameters.FromRoundParameters(roundParams, [ScriptType.Taproot]));

		Assert.Empty(coins);
	}

	/// <summary>
	/// Coins that together are worth less than the smallest output of the round are not registered: they would only pay fees.
	/// </summary>
	[Fact]
	public void SelectNothingFromTooSmallSetOfCoins()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = new[]
		{
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008711m + 0.00005800m)),
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008710m + 0.00005800m))
		};
		var roundParams = WabiSabiFactory.CreateRoundParameters(new()
		{
			MinRegistrableAmount = Money.Coins(0.0001m),
			MaxRegistrableAmount = Money.Coins(430),
		});

		var coins = CoinJoinCoinSelector.SelectCoinsForRound(coinsToSelectFrom, UtxoSelectionParameters.FromRoundParameters(roundParams, [ScriptType.Taproot]));

		Assert.Empty(coins);
	}

	/// <summary>
	/// The coins are registered when they are worth exactly the smallest output that the round makes.
	/// </summary>
	[Fact]
	public void SelectEverythingFromJustEnoughSetOfCoins()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = new[]
		{
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008711m + 0.00005800m)),
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.00008711m + 0.00005800m))
		};
		var roundParams = WabiSabiFactory.CreateRoundParameters(new()
		{
			MinRegistrableAmount = Money.Coins(0.0001m),
			MaxRegistrableAmount = Money.Coins(430),
		});

		var coins = CoinJoinCoinSelector.SelectCoinsForRound(coinsToSelectFrom, UtxoSelectionParameters.FromRoundParameters(roundParams, [ScriptType.Taproot]));

		AssertSameCoins(coinsToSelectFrom, coins);
	}

	private static List<SmartCoin> CreateCoinsOfDifferentAmounts(int count)
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);

		// Not in the order of their amounts, so that the place of a coin among the others says nothing.
		var random = new Random(count);
		return Enumerable
			.Range(1, count)
			.OrderBy(_ => random.Next())
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.1m * i)))
			.ToList();
	}

	/// <summary>The selection has each of the expected coins once and nothing else. Its order is random.</summary>
	private static void AssertSameCoins(IReadOnlyCollection<SmartCoin> expected, IReadOnlyCollection<SmartCoin> selection)
	{
		Assert.Equal(expected.Count, selection.Count);
		Assert.Equal(expected.Select(x => x.Outpoint).ToHashSet(), selection.Select(x => x.Outpoint).ToHashSet());
	}
}
