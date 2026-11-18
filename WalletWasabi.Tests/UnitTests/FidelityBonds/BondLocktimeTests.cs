using System.Linq;
using WalletWasabi.FidelityBonds;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// A fidelity bond unlocks at the start of a month in UTC. The months are numbered as in JoinMarket.
/// </summary>
public class BondLocktimeTests
{
	[Theory]
	[InlineData(0, 2020, 1, 1577836800)]
	[InlineData(12, 2021, 1, 1609459200)]
	[InlineData(73, 2026, 2, 1769904000)]
	[InlineData(216, 2038, 1, 2145916800)]
	[InlineData(217, 2038, 2, 2148595200)]
	[InlineData(959, 2099, 12, 4099766400)]
	public void MonthsAreNumberedFromJanuary2020(int index, int year, int month, long unixTime)
	{
		var locktime = BondLocktime.FromIndex(index);

		Assert.Equal(new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero), locktime.Date);
		Assert.Equal(unixTime, locktime.UnixTime);
		Assert.Equal(unixTime, locktime.LockTime.Value);
		Assert.Equal(locktime, BondLocktime.FromMonth(year, month));

		Assert.True(BondLocktime.TryFromUnixTime(unixTime, out var parsed));
		Assert.Equal(index, parsed.Value.Index);
	}

	[Fact]
	public void ThereAre960Months()
	{
		var all = BondLocktime.All.ToArray();

		Assert.Equal(960, all.Length);
		Assert.Equal(BondLocktime.First, all[0]);
		Assert.Equal(BondLocktime.Last, all[^1]);

		for (var i = 0; i < all.Length; i++)
		{
			Assert.Equal(i, all[i].Index);
			Assert.Equal(1, all[i].Date.Day);
			Assert.Equal(TimeSpan.Zero, all[i].Date.TimeOfDay);
			Assert.Equal(TimeSpan.Zero, all[i].Date.Offset);
			Assert.Equal(all[i], BondLocktime.FromMonth(all[i].Date.Year, all[i].Date.Month));
		}

		Assert.True(all.Zip(all.Skip(1)).All(x => x.First.CompareTo(x.Second) < 0 && x.First.UnixTime < x.Second.UnixTime));
	}

	[Theory]
	[InlineData(1769904001)] // A second after the start of a month.
	[InlineData(1769903999)] // A second before it.
	[InlineData(1769990400)] // The second day of a month.
	[InlineData(1577836799)] // The last second of 2019.
	[InlineData(1575158400)] // December 2019.
	[InlineData(4102444800)] // January 2100.
	[InlineData(0)]
	[InlineData(-1)]
	public void OtherTimesAreNoLocktimes(long unixTime)
	{
		Assert.False(BondLocktime.TryFromUnixTime(unixTime, out var locktime));
		Assert.Null(locktime);
	}

	[Fact]
	public void MonthsOutsideTheRangeAreRefused()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => BondLocktime.FromIndex(-1));
		Assert.Throws<ArgumentOutOfRangeException>(() => BondLocktime.FromIndex(960));
		Assert.Throws<ArgumentOutOfRangeException>(() => BondLocktime.FromMonth(2019, 12));
		Assert.Throws<ArgumentOutOfRangeException>(() => BondLocktime.FromMonth(2100, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => BondLocktime.FromMonth(2026, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => BondLocktime.FromMonth(2026, 13));
	}

	[Fact]
	public void FirstLocktimeAfterAMoment()
	{
		var february = BondLocktime.FromMonth(2026, 2);
		var march = BondLocktime.FromMonth(2026, 3);

		Assert.Equal(february, BondLocktime.FirstAfter(february.Date.AddSeconds(-1)));
		Assert.Equal(march, BondLocktime.FirstAfter(february.Date));
		Assert.Equal(march, BondLocktime.FirstAfter(february.Date.AddDays(10)));

		// The moment may be given in any time zone.
		Assert.Equal(february, BondLocktime.FirstAfter(new DateTimeOffset(2026, 2, 1, 1, 30, 0, TimeSpan.FromHours(2))));
		Assert.Equal(march, BondLocktime.FirstAfter(new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.FromHours(-2))));

		Assert.Equal(BondLocktime.First, BondLocktime.FirstAfter(new DateTimeOffset(2009, 1, 3, 0, 0, 0, TimeSpan.Zero)));
		Assert.Equal(BondLocktime.Last, BondLocktime.FirstAfter(BondLocktime.Last.Date.AddSeconds(-1)));
		Assert.Null(BondLocktime.FirstAfter(BondLocktime.Last.Date));
	}
}
