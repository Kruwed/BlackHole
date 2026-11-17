using WalletWasabi.Fluent.ViewModels.Wallets.Home.History.Details;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The details of a payment tell its state, its block and its time in one sentence.
/// </summary>
public class TransactionDetailsDateTests
{
	// A local time, so that the text does not depend on the time zone of the machine.
	private static readonly DateTimeOffset Date = new(new DateTime(2026, 11, 16, 13, 42, 0, DateTimeKind.Local));

	[Fact]
	public void ConfirmedTransactionTellsItsBlock()
	{
		Assert.Equal(
			"Confirmed in block 976,172 at 13:42 on November 16, 2026",
			TransactionDetailsViewModel.DescribeDate(isConfirmed: true, blockHeight: 976_172, Date));
	}

	[Fact]
	public void SmallBlockHeightHasNoSeparator()
	{
		Assert.Equal(
			"Confirmed in block 117 at 13:42 on November 16, 2026",
			TransactionDetailsViewModel.DescribeDate(isConfirmed: true, blockHeight: 117, Date));
	}

	[Fact]
	public void UnconfirmedTransactionTellsWhenItWasBroadcast()
	{
		Assert.Equal(
			"Broadcast at 13:42 on November 16, 2026",
			TransactionDetailsViewModel.DescribeDate(isConfirmed: false, blockHeight: 0, Date));
	}
}
