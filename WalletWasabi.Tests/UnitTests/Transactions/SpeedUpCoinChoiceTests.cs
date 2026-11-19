using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Transactions;

/// <summary>
/// A transaction that is sped up by spending one of its outputs may need a second coin to pay the fee, and the wallet
/// picks that coin by itself.
/// </summary>
public class SpeedUpCoinChoiceTests
{
	[Fact]
	public void OnlyCoinsThatAreLinkedToTheOutputAlreadyMayBeAdded()
	{
		var keyManager = ServiceFactory.CreateKeyManager();
		var output = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.0001m, confirmed: false);
		var linked = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 1m);
		var unlinked = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 1m);

		// A matched output of a transaction that is worth points.
		var coinjoin = Transaction.Create(Network.Main);
		coinjoin.Outputs.Add(new TxOut(Money.Coins(1m), BitcoinFactory.CreateScript()));
		var coinjoined = BitcoinFactory.CreateSmartCoin(coinjoin, BitcoinFactory.CreateHdPubKey(keyManager), Money.Coins(1m));
		coinjoined.Transaction.AnonymityPoints = 8;
		Assert.Equal(8, coinjoined.AnonymityPoints);

		output.HdPubKey.Cluster.Merge(linked.HdPubKey.Cluster);

		var coins = TransactionModifierWalletExtensions.GetCoinsLinkedTo(output, [unlinked, output, coinjoined, linked]);

		// No anonymity points make a coin one that can be spent together with any other.
		Assert.Equal([output, linked], coins);
	}
}
