using NBitcoin;
using System.Linq;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallets;

public class LockTimeSelectorTests
{
	[Fact]
	public void GetLockTimeBasedOnDistributionTest()
	{
		var lockTimeSelector = new LockTimeSelector(new Random(Seed: 1));

		uint tipHeight = 600_000;
		var lockTimes = Enumerable.Range(0, 20_000)
			.Select(_ => lockTimeSelector.GetLockTimeBasedOnDistribution(tipHeight))
			.ToList();

		// Most transactions have no lock time, some have the tip, a few have a height shortly before the tip.
		Assert.Contains(lockTimes, lockTime => lockTime == LockTime.Zero);
		Assert.Contains(lockTimes, lockTime => lockTime.Value == tipHeight);
		Assert.Contains(lockTimes, lockTime => lockTime.Value != 0 && lockTime.Value < tipHeight);

		Assert.All(lockTimes, lockTime =>
		{
			if (lockTime != LockTime.Zero)
			{
				Assert.True(lockTime.IsHeightLock);

				// Never ahead of the tip: the node does not accept a transaction that is not final yet.
				Assert.InRange(lockTime.Value, tipHeight - 99, tipHeight);
			}
		});
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(50)]
	[InlineData(99)]
	public void LockTimeNeverLeavesAShortChain(uint tipHeight)
	{
		var lockTimeSelector = new LockTimeSelector(new Random(Seed: 2));

		for (var i = 0; i < 20_000; i++)
		{
			// A height before the genesis block would wrap around into a lock time in the far future.
			var lockTime = lockTimeSelector.GetLockTimeBasedOnDistribution(tipHeight);
			Assert.InRange(lockTime.Value, 0u, tipHeight);
		}
	}
}
