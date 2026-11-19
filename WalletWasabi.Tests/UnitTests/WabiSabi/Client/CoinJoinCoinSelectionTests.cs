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
/// Tests for <see cref="CoinJoinCoinSelector"/>: a round gets the coins that the user lets into coinjoins, all of them
/// that it takes.
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
	/// The wallet does not choose among the coins: however many there are, each of them is registered.
	/// </summary>
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(10)]
	[InlineData(11)]
	[InlineData(40)]
	public void SelectsEveryCoin(int coinCount)
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var coinsToSelectFrom = Enumerable
			.Range(0, coinCount)
			.Select(i => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(km), Money.Coins(0.1m * (i + 1))))
			.ToList();

		var coins = CoinJoinCoinSelector.SelectCoinsForRound(coinsToSelectFrom, WabiSabiFactory.CreateUtxoSelectionParameters());

		AssertSameCoins(coinsToSelectFrom, coins);
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
	/// part without it.
	/// </summary>
	[Fact]
	public void LeavesOutTheCoinsThatTheRoundDoesNotTake()
	{
		var km = KeyManager.CreateNew(out _, "", Network.Main);
		var parameters = WabiSabiFactory.CreateUtxoSelectionParameters();
		var taken = Enumerable
			.Range(0, 12)
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

	/// <summary>The selection has each of the expected coins once and nothing else. Its order is random.</summary>
	private static void AssertSameCoins(IReadOnlyCollection<SmartCoin> expected, IReadOnlyCollection<SmartCoin> selection)
	{
		Assert.Equal(expected.Count, selection.Count);
		Assert.Equal(expected.Select(x => x.Outpoint).ToHashSet(), selection.Select(x => x.Outpoint).ToHashSet());
	}
}
