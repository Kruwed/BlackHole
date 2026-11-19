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

		// Whether a coin is kept out of coinjoins is something that its row follows by itself.
		coin.IsExcludedFromCoinJoin = true;
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

		// The coin got another anonymity score.
		coin.SetAnonymitySet(5);
		var coinjoined = CoinViewModel.DescribeContent(coin);
		Assert.NotEqual(confirmed, coinjoined);

		// The coin was banned from coinjoins.
		coin.BannedUntilUtc = DateTimeOffset.UtcNow.AddDays(1);
		Assert.NotEqual(coinjoined, CoinViewModel.DescribeContent(coin));
	}
}
