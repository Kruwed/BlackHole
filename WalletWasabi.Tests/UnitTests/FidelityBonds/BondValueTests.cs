using System.Numerics;
using NBitcoin;
using WalletWasabi.FidelityBonds;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The value of a fidelity bond is calculated as in JoinMarket.
/// </summary>
public class BondValueTests
{
	/// <summary>The expected values were calculated with <c>jmcore/bond_calc.py</c> of JoinMarket NG.</summary>
	[Theory]
	[InlineData(100_000_000, 1672531200, 1704067200, 1672531200, "107840156")] // One bitcoin for a year.
	[InlineData(100_000_000, 1672531200, 1704067200, 1767225600, "0")] // Two years after its locktime.
	[InlineData(100_000_000, 1672531200, 1767225600, 1769904000, "442820884")] // A month after its locktime.
	[InlineData(5_000_000, 1794922927, 1811808000, 1798099200, "970011")]
	[InlineData(123_456_789, 1741493106, 2051222400, 1893456000, "3015861298")]
	[InlineData(2_100_000_000_000_000, 1609459200, 4099766400, 1640995200, "82963121322361847808")] // Every coin there is, until the last month.
	[InlineData(1, 1767225600, 1769904000, 1768435200, "0")]
	[InlineData(250_000_000, 1716199200, 1735689600, 1748736000, "45049019")]
	[InlineData(250_000_000, 1767225600, 1735689600, 1780272000, "0")] // Confirmed after its locktime: nothing was ever locked.
	public void MatchesJoinMarket(long satoshis, long confirmationTime, long locktime, long currentTime, string expected)
	{
		Assert.True(BondLocktime.TryFromUnixTime(locktime, out var bondLocktime));

		var value = BondValue.Calculate(
			Money.Satoshis(satoshis),
			DateTimeOffset.FromUnixTimeSeconds(confirmationTime),
			bondLocktime.Value,
			DateTimeOffset.FromUnixTimeSeconds(currentTime));

		Assert.Equal(BigInteger.Parse(expected), value);
	}

	[Fact]
	public void ValueStaysTheSameUntilTheLocktimeAndWearsOffAfterwards()
	{
		var amount = Money.Coins(1);
		var confirmed = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
		var locktime = BondLocktime.FromMonth(2028, 1);

		var atConfirmation = BondValue.Calculate(amount, confirmed, locktime, confirmed);
		var aYearLater = BondValue.Calculate(amount, confirmed, locktime, confirmed.AddYears(1));
		var atLocktime = BondValue.Calculate(amount, confirmed, locktime, locktime.Date);
		var aMonthAfter = BondValue.Calculate(amount, confirmed, locktime, locktime.Date.AddMonths(1));
		var longAfter = BondValue.Calculate(amount, confirmed, locktime, locktime.Date.AddYears(3));

		Assert.True(atConfirmation > 0);
		Assert.Equal(atConfirmation, aYearLater);
		Assert.Equal(atConfirmation, atLocktime);
		Assert.True(aMonthAfter < atLocktime);
		Assert.True(aMonthAfter > 0);
		Assert.Equal(BigInteger.Zero, longAfter);
	}

	[Fact]
	public void LockingLongerOrMoreIsWorthMore()
	{
		var confirmed = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		var oneYear = BondValue.Calculate(Money.Coins(1), confirmed, BondLocktime.FromMonth(2027, 1), confirmed);
		var twoYears = BondValue.Calculate(Money.Coins(1), confirmed, BondLocktime.FromMonth(2028, 1), confirmed);
		var twoCoins = BondValue.Calculate(Money.Coins(2), confirmed, BondLocktime.FromMonth(2027, 1), confirmed);

		Assert.True(twoYears > oneYear);

		// One bond of two coins is worth more than two bonds of one coin.
		Assert.True(twoCoins > 2 * oneYear);
	}
}
