using NBitcoin;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// A list of coins has one row for every coin. A row is made once, so the list has to tell when a coin needs a new
/// one: otherwise it goes on showing the coin as it was.
/// </summary>
public class CoinListRowTests
{
	[Fact]
	public void CoinKeepsItsRowForAsLongAsNothingChangedThatTheRowShows()
	{
		var keyManager = ServiceFactory.CreateKeyManager();
		var coin = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.1m);
		var content = CoinViewModel.DescribeContent(coin);

		Assert.Equal(content, CoinViewModel.DescribeContent(coin));

		// Whether a coin is chosen for coinjoins is something that its row follows by itself.
		coin.IsSelectedForCoinJoin = true;
		Assert.Equal(content, CoinViewModel.DescribeContent(coin));
	}

	[Fact]
	public void EveryCoinHasARowOfItsOwn()
	{
		var keyManager = ServiceFactory.CreateKeyManager();
		var coin = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.1m);
		var coinOfTheSameAmount = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.1m);

		Assert.NotEqual(CoinViewModel.DescribeContent(coin), CoinViewModel.DescribeContent(coinOfTheSameAmount));
	}

	[Fact]
	public void CoinNeedsANewRowWhenWhatTheRowShowsChanged()
	{
		var keyManager = ServiceFactory.CreateKeyManager();
		var coin = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.1m, confirmed: false);
		var unconfirmed = CoinViewModel.DescribeContent(coin);

		// The coin was confirmed.
		coin.Height = new Height.ChainHeight(100);
		var confirmed = CoinViewModel.DescribeContent(coin);
		Assert.NotEqual(unconfirmed, confirmed);

		// The coin was banned from coinjoins.
		coin.BannedUntilUtc = DateTimeOffset.UtcNow.AddDays(1);
		Assert.NotEqual(confirmed, CoinViewModel.DescribeContent(coin));
	}

	/// <summary>
	/// The anonymity points of a matched output are found out some time after the coin arrived, with the help of the
	/// node. Until then its row shows a question mark, and then the coin needs a new row.
	/// </summary>
	[Fact]
	public void CoinNeedsANewRowWhenItsAnonymityPointsWereFoundOut()
	{
		var keyManager = ServiceFactory.CreateKeyManager();
		var key = BitcoinFactory.CreateHdPubKey(keyManager);
		var transaction = Network.Main.CreateTransaction();
		transaction.Inputs.Add(BitcoinFactory.CreateOutPoint());
		transaction.Outputs.Add(Money.Coins(0.1m), key.P2Taproot);
		transaction.Outputs.Add(Money.Coins(0.1m), BitcoinFactory.CreateScript());
		var smartTransaction = new SmartTransaction(transaction, new Height.ChainHeight(100));
		var coin = new SmartCoin(smartTransaction, 0, key);

		Assert.Null(coin.AnonymityPoints);
		var unknown = CoinViewModel.DescribeContent(coin);

		smartTransaction.AnonymityPoints = 0;
		var none = CoinViewModel.DescribeContent(coin);
		Assert.NotEqual(unknown, none);

		smartTransaction.AnonymityPoints = 4;
		var four = CoinViewModel.DescribeContent(coin);
		Assert.NotEqual(none, four);

		// Half a point more is another number to show.
		smartTransaction.AnonymityPoints = 4.5m;
		Assert.NotEqual(four, CoinViewModel.DescribeContent(coin));
	}

	[Fact]
	public void RowShowsAQuestionMarkWhileTheAnonymityPointsAreNotKnown()
	{
		Assert.Equal("?", CoinViewModel.DescribeAnonymityPoints(null));
		Assert.Equal("0", CoinViewModel.DescribeAnonymityPoints(0));
		Assert.Equal("12", CoinViewModel.DescribeAnonymityPoints(12));
	}

	/// <summary>A transaction can be worth half a point more than a whole number, and the row says so.</summary>
	[Fact]
	public void RowShowsHalfAPoint()
	{
		Assert.Equal("0.5", CoinViewModel.DescribeAnonymityPoints(0.5m));
		Assert.Equal("7.5", CoinViewModel.DescribeAnonymityPoints(7.5m));
		Assert.Equal("7", CoinViewModel.DescribeAnonymityPoints(7.0m));
	}
}
