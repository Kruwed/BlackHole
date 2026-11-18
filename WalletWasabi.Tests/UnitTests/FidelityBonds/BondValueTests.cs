using System.Numerics;
using NBitcoin;
using WalletWasabi.FidelityBonds;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The value of a fidelity bond is the one of JoinMarket's formula, with the time counted in blocks.
/// </summary>
public class BondValueTests
{
	/// <remarks>The expected values were calculated apart from the wallet, with the formula written out in Python.</remarks>
	[Theory]
	[InlineData(100_000_000, 900_000u, 952_595u, 900_000u, "107934222")] // A year, just confirmed
	[InlineData(100_000_000, 900_000u, 952_595u, 952_595u, "107934222")] // The same until it unlocks
	[InlineData(100_000_000, 900_000u, 952_595u, 960_000u, "88732189")] // Then it wears off
	[InlineData(100_000_000, 900_000u, 952_595u, 1_005_190u, "0")] // As long after as it was locked for: nothing is left
	[InlineData(30_000_000, 118u, 2_000u, 130u, "294514")]
	[InlineData(5_000_000, 976_172u, 1_028_732u, 980_000u, "2195035")]
	[InlineData(123_456_789, 850_000u, 1_375_600u, 1_000_000u, "3092838373")] // Ten years
	[InlineData(2_100_000_000_000_000, 800_000u, 1_325_600u, 900_000u, "7767349336192883712")] // Every bitcoin there is
	[InlineData(1, 900_000u, 900_144u, 900_010u, "0")]
	[InlineData(250_000_000, 950_000u, 940_000u, 960_000u, "0")] // Confirmed when it was unlocked already
	[InlineData(250_000_000, 900_000u, 900_001u, 900_000u, "256")] // Locked for a single block
	public void ValueFollowsTheFormulaOfJoinMarketCountedInBlocks(long satoshis, uint confirmationHeight, uint unlockHeight, uint currentHeight, string expected)
	{
		Assert.Equal(BigInteger.Parse(expected), BondValue.Calculate(Money.Satoshis(satoshis), confirmationHeight, unlockHeight, currentHeight));
	}

	[Fact]
	public void YearIsCountedInBlocksOfTenMinutes()
	{
		Assert.Equal(52_594.92, BondValue.BlocksPerYear, precision: 6);
	}

	[Fact]
	public void OneLargeBondIsWorthMoreThanTheSameCoinsInSmallOnes()
	{
		var large = BondValue.Calculate(Money.Coins(1), 900_000, 950_000, 900_000);
		var small = BondValue.Calculate(Money.Coins(0.25m), 900_000, 950_000, 900_000);

		Assert.True(large > 4 * small);
	}

	[Fact]
	public void LongerLockIsWorthMore()
	{
		var year = BondValue.Calculate(Money.Coins(1), 900_000, 952_595, 900_000);
		var twoYears = BondValue.Calculate(Money.Coins(1), 900_000, 1_005_190, 900_000);

		Assert.True(twoYears > year);

		// The value does not depend on when it is asked for, as long as the bond is locked.
		Assert.Equal(year, BondValue.Calculate(Money.Coins(1), 900_000, 952_595, 930_000));
	}
}
