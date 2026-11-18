using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// A list of coins shows the coins of a privacy level as a group with one row. The row is made once, so the list has
/// to tell when a group needs a new one: otherwise it goes on showing the coins that the group had before.
/// </summary>
public class CoinListGroupTests
{
	[Fact]
	public void GroupOfCoinsIsTheSameRowForAsLongAsItsCoinsAreTheSame()
	{
		var keyManager = ServiceFactory.CreateKeyManager();
		var first = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.1m);
		var second = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.2m);

		var content = CoinGroupViewModel.DescribeContent([first, second]);

		// The order in which the wallet lists the coins does not matter.
		Assert.Equal(content, CoinGroupViewModel.DescribeContent([second, first]));
		Assert.Equal(content, CoinGroupViewModel.DescribeContent(new List<SmartCoin> { first, second }.AsEnumerable()));
	}

	[Fact]
	public void GroupOfCoinsNeedsANewRowWhenItsCoinsChanged()
	{
		var keyManager = ServiceFactory.CreateKeyManager();
		var first = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.1m, confirmed: false);
		var second = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.2m);
		var third = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.3m);

		var content = CoinGroupViewModel.DescribeContent([first, second]);

		// A coin arrived, a coin was spent.
		Assert.NotEqual(content, CoinGroupViewModel.DescribeContent([first, second, third]));
		Assert.NotEqual(content, CoinGroupViewModel.DescribeContent([first]));
		Assert.NotEqual(content, CoinGroupViewModel.DescribeContent([first, third]));

		// A coin was confirmed: the row of the group tells whether all of its coins are.
		first.Height = new Height.ChainHeight(100);
		var confirmed = CoinGroupViewModel.DescribeContent([first, second]);
		Assert.NotEqual(content, confirmed);

		// A coin got another anonymity score: the row of the group tells the score that its coins share.
		second.SetAnonymitySet(5);
		Assert.NotEqual(confirmed, CoinGroupViewModel.DescribeContent([first, second]));
	}
}
