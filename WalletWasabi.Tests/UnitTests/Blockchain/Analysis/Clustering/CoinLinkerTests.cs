using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Blockchain.Analysis.Clustering;

/// <summary>
/// Tests for <see cref="CoinLinker"/>: which coins of the wallet a transaction links to each other.
/// </summary>
public class CoinLinkerTests
{
	private readonly KeyManager _keyManager = ServiceFactory.CreateKeyManager();

	[Fact]
	public void ChangeOfAPaymentIsLinkedToTheCoinsThatPaid()
	{
		var first = CreateCoin(1m);
		var second = CreateCoin(2m);
		var (tx, outputs) = CreateTransaction([first, second], othersInputs: 0, othersOutputs: [Money.Coins(2.5m)], ownOutputs: [Money.Coins(0.4m)]);

		CoinLinker.LinkOutputsToInputs(tx);

		var change = Assert.Single(outputs);
		AssertCluster(change, first.HdPubKey, second.HdPubKey, change.HdPubKey);

		// The coins that were spent together are linked to each other by that, too.
		Assert.Equal(first.HdPubKey.Cluster, second.HdPubKey.Cluster);
	}

	[Fact]
	public void ReceivedCoinIsLinkedToNothing()
	{
		var (tx, outputs) = CreateTransaction([], othersInputs: 2, othersOutputs: [Money.Coins(3m)], ownOutputs: [Money.Coins(1m), Money.Coins(2m)]);

		CoinLinker.LinkOutputsToInputs(tx);

		Assert.All(outputs, coin => AssertCluster(coin, coin.HdPubKey));
	}

	/// <summary>
	/// An output whose amount another output of the transaction has too is not linked to the inputs of the wallet,
	/// whoever the other output belongs to. What is left over as an amount of its own is.
	/// </summary>
	[Fact]
	public void MatchedOutputsAreNotLinkedButTheLeftoverIs()
	{
		var first = CreateCoin(1m);
		var second = CreateCoin(0.7m);
		var (tx, outputs) = CreateTransaction(
			[first, second],
			othersInputs: 5,
			othersOutputs: [Money.Coins(0.5m), Money.Coins(0.5m), Money.Coins(0.25m), Money.Coins(0.3m)],
			ownOutputs: [Money.Coins(0.5m), Money.Coins(0.25m), Money.Coins(0.2m), Money.Coins(0.2m), Money.Coins(0.54m)]);

		CoinLinker.LinkOutputsToInputs(tx);

		var leftover = Assert.Single(outputs, coin => coin.Amount == Money.Coins(0.54m));
		Assert.False(leftover.IsMatchedOutput);
		AssertCluster(leftover, first.HdPubKey, second.HdPubKey, leftover.HdPubKey);

		// Matched with outputs of others, and matched with each other only.
		var matched = outputs.Where(coin => coin != leftover).ToArray();
		Assert.Equal(4, matched.Length);
		Assert.All(matched, coin => Assert.True(coin.IsMatchedOutput));
		Assert.All(matched, coin => AssertCluster(coin, coin.HdPubKey));
	}

	[Fact]
	public void LinkingAgainChangesNothing()
	{
		var spent = CreateCoin(1m);
		var (tx, outputs) = CreateTransaction([spent], othersInputs: 0, othersOutputs: [Money.Coins(0.6m)], ownOutputs: [Money.Coins(0.3m)]);

		CoinLinker.LinkOutputsToInputs(tx);
		CoinLinker.LinkOutputsToInputs(tx);

		var change = Assert.Single(outputs);
		AssertCluster(change, spent.HdPubKey, change.HdPubKey);
	}

	private SmartCoin CreateCoin(decimal amountBtc) =>
		BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(_keyManager), amountBtc);

	/// <summary>Creates a transaction with all of its inputs and outputs in place, and the coins that it gives to the wallet.</summary>
	private (SmartTransaction Transaction, SmartCoin[] WalletOutputs) CreateTransaction(SmartCoin[] ownInputs, int othersInputs, Money[] othersOutputs, Money[] ownOutputs)
	{
		var tx = Transaction.Create(Network.Main);

		foreach (var input in ownInputs)
		{
			tx.Inputs.Add(input.Outpoint);
		}

		for (var i = 0; i < othersInputs; i++)
		{
			tx.Inputs.Add(BitcoinFactory.CreateOutPoint());
		}

		foreach (var amount in othersOutputs)
		{
			tx.Outputs.Add(new TxOut(amount, BitcoinFactory.CreateScript()));
		}

		var ownKeys = ownOutputs.Select(_ => BitcoinFactory.CreateHdPubKey(_keyManager)).ToArray();
		foreach (var (amount, key) in ownOutputs.Zip(ownKeys))
		{
			tx.Outputs.Add(new TxOut(amount, key.P2Taproot));
		}

		var transaction = new SmartTransaction(tx, new Height.ChainHeight(100));

		foreach (var input in ownInputs)
		{
			transaction.TryAddWalletInput(input);
		}

		// A coin adds itself to the outputs of the wallet in its transaction.
		var walletOutputs = ownKeys
			.Select((key, i) => new SmartCoin(transaction, (uint)(othersOutputs.Length + i), key))
			.ToArray();

		return (transaction, walletOutputs);
	}

	/// <summary>Asserts that the cluster of the coin consists of exactly the given keys.</summary>
	private static void AssertCluster(SmartCoin coin, params HdPubKey[] keys) =>
		Assert.Equal(new Cluster([.. keys]), coin.HdPubKey.Cluster);
}
