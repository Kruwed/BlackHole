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
		var coinjoined = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 1m, anonymitySet: 100);
		output.HdPubKey.Cluster.Merge(linked.HdPubKey.Cluster);

		var coins = TransactionModifierWalletExtensions.GetCoinsLinkedTo(output, [unlinked, output, coinjoined, linked]);

		// No anonymity score makes a coin one that can be spent together with any other.
		Assert.Equal([output, linked], coins);
	}
}
