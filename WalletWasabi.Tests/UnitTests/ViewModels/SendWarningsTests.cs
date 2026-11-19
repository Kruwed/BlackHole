using NBitcoin;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// What the preview of a transaction warns about. The warnings do not ask how private a coin is: they tell what the
/// transaction gives away, whatever the anonymity scores of the coins are.
/// </summary>
public class SendWarningsTests
{
	[Fact]
	public void TransactionThatSpendsCoinsOfOneClusterLinksNothingNew()
	{
		var (first, second) = CreateTwoCoins(firstScore: 1, secondScore: 1);

		Assert.False(PrivacyWarningsModel.LinksCoins([]));
		Assert.False(PrivacyWarningsModel.LinksCoins([first]));

		// The coins were spent together before, or one is the change of the other.
		first.HdPubKey.Cluster.Merge(second.HdPubKey.Cluster);
		Assert.False(PrivacyWarningsModel.LinksCoins([first, second]));
	}

	[Theory]
	[InlineData(1, 1)]
	[InlineData(1, 5)]
	[InlineData(5, 50)]
	[InlineData(50, 50)]
	public void TransactionLinksCoinsThatNothingLinkedBeforeWhateverTheirAnonymityScores(int firstScore, int secondScore)
	{
		var (first, second) = CreateTwoCoins(firstScore, secondScore);

		Assert.True(PrivacyWarningsModel.LinksCoins([first, second]));
	}

	[Theory]
	[InlineData(95_000_000, 100_000_000, 95)]
	[InlineData(99_900_000, 100_000_000, 99)]
	[InlineData(100_000_000, 100_000_000, 100)]
	[InlineData(1_000_001, 1_000_001, 100)]
	public void SpendingNearlyAllOfTheBalanceIsToldInPercent(long spentSatoshis, long balanceSatoshis, int expectedPercent)
	{
		Assert.Equal(expectedPercent, PrivacyWarningsModel.GetLargePercentSpent(Money.Satoshis(spentSatoshis), Money.Satoshis(balanceSatoshis)));
	}

	[Theory]
	[InlineData(90_000_000, 100_000_000)] // Nine tenths of the balance are not more than nine tenths.
	[InlineData(50_000_000, 100_000_000)]
	[InlineData(1_000_000, 1_000_000)] // All of the balance, but not more than 0.01 BTC.
	[InlineData(900_000, 900_000)]
	[InlineData(0, 0)]
	[InlineData(5_000_000, 0)] // The coins were spent in the meantime.
	public void SpendingLessOrLittleIsNotTold(long spentSatoshis, long balanceSatoshis)
	{
		Assert.Null(PrivacyWarningsModel.GetLargePercentSpent(Money.Satoshis(spentSatoshis), Money.Satoshis(balanceSatoshis)));
	}

	private static (SmartCoin First, SmartCoin Second) CreateTwoCoins(int firstScore, int secondScore)
	{
		var keyManager = ServiceFactory.CreateKeyManager();

		return (
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.1m, anonymitySet: firstScore),
			BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), 0.2m, anonymitySet: secondScore));
	}
}
