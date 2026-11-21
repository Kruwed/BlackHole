using System.Numerics;
using NBitcoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Bonds;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History.Details;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// What the pages of the fidelity bonds tell about a bond, which unlock heights and fee rates they take, and how the
/// history and the notifications name the transactions of a bond.
/// </summary>
public class FidelityBondPagesTests
{
	private const uint UnlockHeight = 1_028_732;
	private static readonly Network Network = Network.RegTest;

	// A local time, so that the texts do not depend on the time zone of the machine. Noon, so that an hour more or less
	// of daylight saving time does not change the day.
	private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local));

	[Theory]
	[InlineData("2026-11-18T03:30:00Z", "2026-12-01T00:00:00Z", "12 days")]
	[InlineData("2026-11-17T00:00:00Z", "2027-01-01T00:00:00Z", "1 month and 15 days")]
	[InlineData("2026-12-01T00:00:00Z", "2027-01-01T00:00:00Z", "1 month")]
	[InlineData("2026-12-01T00:00:00Z", "2027-12-01T00:00:00Z", "1 year")]
	[InlineData("2026-12-01T00:00:00Z", "2028-03-01T00:00:00Z", "1 year and 3 months")]
	[InlineData("2026-11-17T12:00:00Z", "2036-11-01T00:00:00Z", "9 years and 11 months")]
	[InlineData("2026-11-30T00:00:00Z", "2026-12-01T00:00:00Z", "1 day")]
	[InlineData("2026-11-30T00:00:01Z", "2026-12-01T00:00:00Z", "less than a day")]
	[InlineData("2026-12-01T00:00:00Z", "2026-12-01T00:00:00Z", "no time")]
	[InlineData("2026-12-02T00:00:00Z", "2026-12-01T00:00:00Z", "no time")]
	public void DurationIsToldByTheCalendar(string from, string to, string expected)
	{
		Assert.Equal(expected, DurationText.Between(DateTimeOffset.Parse(from), DateTimeOffset.Parse(to)));
	}

	[Theory]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-18T12:10:00Z", "10 minutes")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-18T12:01:00Z", "1 minute")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-18T12:00:10Z", "1 minute")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-18T13:00:00Z", "1 hour")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-18T15:40:00Z", "3 hours and 40 minutes")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-19T11:50:00Z", "23 hours and 50 minutes")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-19T12:00:00Z", "1 day")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-20T01:40:00Z", "1 day and 14 hours")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-19T13:20:00Z", "1 day and 1 hour")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-20T12:10:00Z", "2 days")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-21T11:00:00Z", "2 days and 23 hours")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-21T12:00:00Z", "3 days")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-22T20:00:00Z", "4 days")]
	[InlineData("2026-11-18T12:00:00Z", "2026-11-18T12:00:00Z", "no time")]
	public void ShortDurationCanBeToldWithItsHours(string from, string to, string expected)
	{
		Assert.Equal(expected, DurationText.Between(DateTimeOffset.Parse(from), DateTimeOffset.Parse(to), withHours: true));
	}

	[Fact]
	public void DurationDoesNotDependOnTheTimeZone()
	{
		var from = new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.FromHours(-5)); // December 1, 04:00 UTC
		var to = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

		Assert.Equal("30 days", DurationText.Between(from, to));
	}

	[Fact]
	public void HeightIsWrittenWithSeparators()
	{
		Assert.Equal("1,028,732", BlockHeightText.Format(1_028_732));
		Assert.Equal("150", BlockHeightText.Format(150));
		Assert.Equal("1 block", BlockHeightText.Count(1));
		Assert.Equal("52,560 blocks", BlockHeightText.Count(52_560));
	}

	[Fact]
	public void HeightIsExpectedTenMinutesAfterTheOneBefore()
	{
		Assert.Equal(Now, BlockHeightText.Estimate(976_172, chainHeight: 976_172, Now));
		Assert.Equal(Now.AddMinutes(10), BlockHeightText.Estimate(976_173, 976_172, Now));
		Assert.Equal(Now.AddDays(1), BlockHeightText.Estimate(976_172 + 144, 976_172, Now));
		Assert.Equal(Now.AddDays(365), BlockHeightText.Estimate(UnlockHeight, 976_172, Now));

		// A height that the chain is past already was reached that long ago, by the same reckoning.
		Assert.Equal(Now.AddHours(-1), BlockHeightText.Estimate(976_166, 976_172, Now));
	}

	[Fact]
	public void ExpectedDayOfAHeightIsToldAndItsHourWhenItIsNear()
	{
		// A year ahead, only the day means something.
		Assert.Equal("November 18, 2027", BlockHeightText.DescribeExpectedTime(UnlockHeight, 976_172, Now));
		Assert.Equal("November 21, 2026", BlockHeightText.DescribeExpectedTime(976_172 + 432, 976_172, Now));

		// Within two days the hour is told too.
		Assert.Equal("13:00 on November 18, 2026", BlockHeightText.DescribeExpectedTime(976_178, 976_172, Now));
		Assert.Equal("12:00 on November 20, 2026", BlockHeightText.DescribeExpectedTime(976_172 + 288, 976_172, Now));
	}

	[Fact]
	public void LockIsDescribedWithItsHeightItsExpectedDayAndItsLength()
	{
		Assert.Equal(
			"Locked until block 1,028,732, expected around November 18, 2027. That is 52,560 blocks from now, about 1 year.",
			BlockHeightText.DescribeLock(UnlockHeight, 976_172, Now));

		Assert.Equal(
			"Locked until block 976,178, expected around 13:00 on November 18, 2026. That is 6 blocks from now, about 1 hour.",
			BlockHeightText.DescribeLock(976_178, 976_172, Now));

		Assert.Equal(
			"Locked until block 976,173, expected around 12:10 on November 18, 2026. That is 1 block from now, about 10 minutes.",
			BlockHeightText.DescribeLock(976_173, 976_172, Now));

		Assert.Equal(
			"Locked until block 976,398, expected around 01:40 on November 20, 2026. That is 226 blocks from now, about 1 day and 14 hours.",
			BlockHeightText.DescribeLock(976_398, 976_172, Now));

		Assert.Equal(
			"Locked until block 976,172, which the chain has reached already.",
			BlockHeightText.DescribeLock(976_172, 976_172, Now));
	}

	[Fact]
	public void CoinsAreLockedUntilABlockThatIsStillToComeAndNoFurtherThanTenYears()
	{
		const uint ChainHeight = 976_172;

		Assert.Null(WalletBondsModel.ValidateUnlockHeight(ChainHeight + 1, ChainHeight));
		Assert.Null(WalletBondsModel.ValidateUnlockHeight(UnlockHeight, ChainHeight));
		Assert.Null(WalletBondsModel.ValidateUnlockHeight(ChainHeight + WalletBondsModel.MaxLockBlocks, ChainHeight));

		// The block that the chain is at, and every block before it, is there already.
		Assert.Contains("976,172", WalletBondsModel.ValidateUnlockHeight(ChainHeight, ChainHeight));
		Assert.NotNull(WalletBondsModel.ValidateUnlockHeight(1, ChainHeight));
		Assert.NotNull(WalletBondsModel.ValidateUnlockHeight(0, ChainHeight));

		// Ten years of blocks is as far as the wallet goes, and as far as a recovered wallet looks for its bonds.
		Assert.Equal(525_600u, WalletBondsModel.MaxLockBlocks);
		Assert.Equal(FidelityBondKeys.MaxLockBlocks, WalletBondsModel.MaxLockBlocks);
		Assert.Contains("1,501,772", WalletBondsModel.ValidateUnlockHeight(ChainHeight + WalletBondsModel.MaxLockBlocks + 1, ChainHeight));
		Assert.NotNull(WalletBondsModel.ValidateUnlockHeight(uint.MaxValue, ChainHeight));
	}

	/// <summary>A new bond locks the coins that are ticked on the Coins tab. Without a ticked coin the page tells what to do.</summary>
	[Fact]
	public void NewBondLocksTheCoinsThatAreTicked()
	{
		Assert.Equal(
			"The bond locks the coin that is ticked on the Coins tab, or a part of it. It holds 0.50 000 000 BTC.",
			CreateBondViewModel.DescribeCoins(tickedCount: 1, Money.Coins(0.5m), walletHasCoins: true));
		Assert.Equal(
			"The bond locks the 3 coins that are ticked on the Coins tab, or a part of them. They hold 1.20 000 000 BTC.",
			CreateBondViewModel.DescribeCoins(tickedCount: 3, Money.Coins(1.2m), walletHasCoins: true));

		Assert.Equal(
			"No coin is ticked. Close this page, tick the coins to lock on the Coins tab and come back.",
			CreateBondViewModel.DescribeCoins(tickedCount: 0, Money.Zero, walletHasCoins: true));
		Assert.Equal(
			"This wallet has no coins to lock.",
			CreateBondViewModel.DescribeCoins(tickedCount: 0, Money.Zero, walletHasCoins: false));
	}

	[Theory]
	[InlineData("1028732", 1_028_732u)]
	[InlineData("1,028,732", 1_028_732u)]
	[InlineData("1 028 732", 1_028_732u)]
	[InlineData("150", 150u)]
	[InlineData("0", 0u)]
	public void HeightIsReadAsItIsTyped(string text, uint expected)
	{
		Assert.Equal(expected, CreateBondViewModel.ParseHeight(text));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("abc")]
	[InlineData("-5")]
	[InlineData("+5")]
	[InlineData("1.5")]
	[InlineData("1e6")]
	[InlineData("0x10")]
	[InlineData("99999999999")] // More than a height can be
	public void TextThatIsNoHeightIsRefused(string text)
	{
		Assert.Null(CreateBondViewModel.ParseHeight(text));
	}

	[Fact]
	public void StateOfABondFollowsItsTransactionsAndTheHeightOfTheChain()
	{
		var unconfirmed = CreateBond(confirmed: false);
		Assert.Equal(FidelityBondState.Unconfirmed, WalletBondsModel.GetState(unconfirmed, UnlockHeight - 10));
		Assert.Equal(FidelityBondState.Unconfirmed, WalletBondsModel.GetState(unconfirmed, UnlockHeight + 10));

		// With the block of the unlock height the node takes the transaction that redeems the bond.
		var confirmed = CreateBond(confirmed: true);
		Assert.Equal(FidelityBondState.Locked, WalletBondsModel.GetState(confirmed, 100));
		Assert.Equal(FidelityBondState.Locked, WalletBondsModel.GetState(confirmed, UnlockHeight - 1));
		Assert.Equal(FidelityBondState.Unlocked, WalletBondsModel.GetState(confirmed, UnlockHeight));
		Assert.Equal(FidelityBondState.Unlocked, WalletBondsModel.GetState(confirmed, UnlockHeight + 1));

		// Without the height of the chain there is no telling, and then the bond is not offered for redemption.
		Assert.Equal(FidelityBondState.Locked, WalletBondsModel.GetState(confirmed, nodeHeight: null));

		var redeeming = confirmed with { SpenderTransaction = new SmartTransaction(Network.CreateTransaction(), Height.Mempool) };
		Assert.Equal(FidelityBondState.Redeeming, WalletBondsModel.GetState(redeeming, UnlockHeight));

		var redeemed = confirmed with { SpenderTransaction = new SmartTransaction(Network.CreateTransaction(), new Height.ChainHeight(1_030_000)) };
		Assert.Equal(FidelityBondState.Redeemed, WalletBondsModel.GetState(redeemed, UnlockHeight));
		Assert.Equal(FidelityBondState.Redeemed, WalletBondsModel.GetState(redeemed, nodeHeight: null));
	}

	[Fact]
	public void ValueOfABondIsCountedFromTheBlockThatConfirmedIt()
	{
		// Confirmed in block 976,172 and locked for a year of blocks.
		var bond = CreateBond(confirmed: true);
		var expected = BondValue.Calculate(bond.Amount, confirmationHeight: 976_172, UnlockHeight, currentHeight: 980_000);

		Assert.Equal(expected, WalletBondsModel.GetValue(bond, 980_000));
		Assert.True(expected > 0);

		// It is not told for a bond that is not confirmed or that is spent, and not without the height of the chain.
		Assert.Null(WalletBondsModel.GetValue(bond, nodeHeight: null));
		Assert.Null(WalletBondsModel.GetValue(CreateBond(confirmed: false), 980_000));
		Assert.Null(WalletBondsModel.GetValue(bond with { SpenderTransaction = new SmartTransaction(Network.CreateTransaction(), Height.Mempool) }, 1_030_000));
	}

	[Fact]
	public void BondInTheListTellsItsStateItsHeightAndItsValue()
	{
		const uint ChainHeight = 976_172;

		var unconfirmed = CreateItem(FidelityBondState.Unconfirmed, value: null, ChainHeight);
		Assert.Equal("Waiting for confirmation", unconfirmed.StateText);
		Assert.Equal("Unlocks at block 1,028,732, expected around November 18, 2027", unconfirmed.HeightText);
		Assert.Null(unconfirmed.ValueText);
		Assert.False(unconfirmed.IsRedeemable);
		Assert.False(unconfirmed.IsOver);

		var locked = CreateItem(FidelityBondState.Locked, new BigInteger(288_248), ChainHeight);
		Assert.Equal("Locked for 52,560 blocks", locked.StateText);
		Assert.Equal("Unlocks at block 1,028,732, expected around November 18, 2027", locked.HeightText);
		Assert.Equal("288,248", locked.ValueText);
		Assert.False(locked.IsRedeemable);

		Assert.Equal("Locked for 1 block", CreateItem(FidelityBondState.Locked, value: null, UnlockHeight - 1).StateText);

		// The node does not tell how far the chain is.
		var unknown = CreateItem(FidelityBondState.Locked, value: null, chainHeight: null);
		Assert.Equal("Locked", unknown.StateText);
		Assert.Equal("Unlocks at block 1,028,732", unknown.HeightText);

		var unlocked = CreateItem(FidelityBondState.Unlocked, new BigInteger(259_451), UnlockHeight + 5);
		Assert.Equal("Unlocked", unlocked.StateText);
		Assert.Equal("Unlocked since block 1,028,732", unlocked.HeightText);
		Assert.True(unlocked.IsRedeemable);
		Assert.False(unlocked.IsOver);

		var redeeming = CreateItem(FidelityBondState.Redeeming, value: null, UnlockHeight + 5);
		Assert.Equal("Redemption waits for confirmation", redeeming.StateText);
		Assert.Equal("Was locked until block 1,028,732", redeeming.HeightText);
		Assert.False(redeeming.IsRedeemable);
		Assert.True(redeeming.IsOver);

		var redeemed = CreateItem(FidelityBondState.Redeemed, value: null, UnlockHeight + 5);
		Assert.Equal("Redeemed", redeemed.StateText);
		Assert.True(redeemed.IsOver);
	}

	[Fact]
	public void ValueOfABondIsWrittenInFullHoweverLargeItIs()
	{
		var item = CreateItem(FidelityBondState.Locked, BigInteger.Parse("82963121322361847808"), chainHeight: null);

		Assert.Equal("82,963,121,322,361,847,808", item.ValueText);
	}

	[Fact]
	public void RedeemButtonOfABondHandsTheBondOver()
	{
		FidelityBondModel? redeemed = null;
		var model = new FidelityBondModel(CreateBond(confirmed: true), CreateAddress(), FidelityBondState.Unlocked, Value: null);
		var item = new BondItemViewModel(model, UnlockHeight, Now, x => redeemed = x);

		item.RedeemCommand.Execute(null);

		Assert.Same(model, redeemed);
	}

	[Theory]
	[InlineData("1", 1)]
	[InlineData("2.5", 2.5)]
	[InlineData("0.1", 0.1)]
	[InlineData("144.71", 144.71)]
	[InlineData("10000", 10000)]
	public void FeeRateThatATransactionCanPayIsTaken(string text, decimal expected)
	{
		Assert.Equal(new FeeRate(expected), RedeemBondViewModel.ParseFeeRate(text));
	}

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("0")]
	[InlineData("0.09")] // Less than nodes relay.
	[InlineData("10000.01")] // More than anybody means to pay.
	[InlineData("-1")]
	[InlineData("1,5")]
	[InlineData("1e3")]
	public void FeeRateThatIsNoneOrOutOfBoundsIsRefused(string text)
	{
		Assert.Null(RedeemBondViewModel.ParseFeeRate(text));
	}

	[Fact]
	public void EstimationThatWasPickedStaysPickedWhenTheEstimationsChange()
	{
		FeeRateOption[] before = [new(2, new FeeRate(100m)), new(6, new FeeRate(20m)), new(36, new FeeRate(3m))];
		FeeRateOption[] after = [new(2, new FeeRate(140m)), new(6, new FeeRate(35m)), new(36, new FeeRate(4m))];

		// Nothing was picked yet: the number of blocks of the last payment decides.
		Assert.Equal(before[1], RedeemBondViewModel.SelectOption(before, picked: null, preferredBlockTarget: 6));
		Assert.Equal(before[2], RedeemBondViewModel.SelectOption(before, picked: null, preferredBlockTarget: 144));

		// The pick is kept by its number of blocks, with the fee rate of the new estimation.
		Assert.Equal(after[2], RedeemBondViewModel.SelectOption(after, picked: before[2], preferredBlockTarget: 6));

		Assert.Null(RedeemBondViewModel.SelectOption([], picked: before[2], preferredBlockTarget: 6));
	}

	[Fact]
	public void TransactionOfABondIsALockOrAnUnlockInTheHistory()
	{
		var fee = Money.Satoshis(30_679);
		var bond = Money.Coins(0.3m);

		// The wallet locks its own coins: what it owns changes by the mining fee, which would pass for a transfer to itself.
		Assert.Equal(TransactionType.Lock, TransactionTreeBuilder.GetItemType(-fee, fee, isCancellation: false, isCpfp: false, lockedInBonds: bond, unlockedFromBonds: Money.Zero));
		Assert.Equal(TransactionType.SelfTransferTransaction, TransactionTreeBuilder.GetItemType(-fee, fee, false, false, Money.Zero, Money.Zero));

		// A bond that is paid from elsewhere comes in, and it is a lock all the same.
		Assert.Equal(TransactionType.Lock, TransactionTreeBuilder.GetItemType(bond, null, false, false, bond, Money.Zero));
		Assert.Equal(TransactionType.IncomingTransaction, TransactionTreeBuilder.GetItemType(bond, null, false, false, Money.Zero, Money.Zero));

		// Redeemed to the wallet or to elsewhere, it is an unlock.
		Assert.Equal(TransactionType.Unlock, TransactionTreeBuilder.GetItemType(-fee, fee, false, false, Money.Zero, bond));
		Assert.Equal(TransactionType.Unlock, TransactionTreeBuilder.GetItemType(-bond, null, false, false, Money.Zero, bond));
		Assert.Equal(TransactionType.OutgoingTransaction, TransactionTreeBuilder.GetItemType(-bond, fee, false, false, Money.Zero, Money.Zero));

		// A bond that goes straight into another bond is locked again.
		Assert.Equal(TransactionType.Lock, TransactionTreeBuilder.GetItemType(-fee, fee, false, false, bond - fee, bond));

		// What cancels a lock or pushes it has no bond in it, and is what it was before.
		Assert.Equal(TransactionType.Cancellation, TransactionTreeBuilder.GetItemType(-fee, fee, isCancellation: true, false, Money.Zero, Money.Zero));
		Assert.Equal(TransactionType.CPFP, TransactionTreeBuilder.GetItemType(-fee, fee, false, isCpfp: true, Money.Zero, Money.Zero));
	}

	[Fact]
	public void DetailsOfALockOrAnUnlockTellTheAmountOfTheBond()
	{
		var fee = Money.Satoshis(30_679);
		var bond = Money.Coins(0.3m);

		Assert.Equal((bond, "Amount locked"), TransactionDetailsViewModel.DescribeAmount(TransactionType.Lock, -fee, fee, bond, Money.Zero));
		Assert.Equal((bond, "Amount locked"), TransactionDetailsViewModel.DescribeAmount(TransactionType.Lock, bond, null, bond, Money.Zero));
		Assert.Equal((bond, "Amount unlocked"), TransactionDetailsViewModel.DescribeAmount(TransactionType.Unlock, -fee, fee, Money.Zero, bond));

		// Any other transaction tells what was sent, without the fee, or what was received.
		Assert.Equal((bond, "Amount sent"), TransactionDetailsViewModel.DescribeAmount(TransactionType.OutgoingTransaction, -(bond + fee), fee, Money.Zero, Money.Zero));
		Assert.Equal((bond, "Amount received"), TransactionDetailsViewModel.DescribeAmount(TransactionType.IncomingTransaction, bond, null, Money.Zero, Money.Zero));
	}

	[Fact]
	public void NotificationTellsOfALockOrAnUnlockAndNotOfAPayment()
	{
		var bond = Money.Coins(0.3m);

		Assert.Equal("0.3000 0000 BTC locked in a fidelity bond".Replace("0.3000 0000", bond.ToFormattedString()), NotificationHelpers.DescribeBondTransaction(bond, Money.Zero, isNew: true, isConfirmation: false));
		Assert.Equal($"{bond.ToFormattedString()} BTC unlocked from a fidelity bond", NotificationHelpers.DescribeBondTransaction(Money.Zero, bond, isNew: true, isConfirmation: false));
		Assert.Equal($"Locking {bond.ToFormattedString()} BTC in a fidelity bond has been confirmed", NotificationHelpers.DescribeBondTransaction(bond, Money.Zero, isNew: false, isConfirmation: true));
		Assert.Equal($"Unlocking {bond.ToFormattedString()} BTC from a fidelity bond has been confirmed", NotificationHelpers.DescribeBondTransaction(Money.Zero, bond, isNew: false, isConfirmation: true));

		// Nothing new about it, or no bond in it: nothing to tell here.
		Assert.Null(NotificationHelpers.DescribeBondTransaction(bond, Money.Zero, isNew: false, isConfirmation: false));
		Assert.Null(NotificationHelpers.DescribeBondTransaction(Money.Zero, Money.Zero, isNew: true, isConfirmation: false));
	}

	private static BondItemViewModel CreateItem(FidelityBondState state, BigInteger? value, uint? chainHeight) =>
		new(new FidelityBondModel(CreateBond(confirmed: state != FidelityBondState.Unconfirmed), CreateAddress(), state, value), chainHeight, Now, _ => { });

	private static BitcoinAddress CreateAddress()
	{
		using var key = new Key();
		return BondScript.GetAddress(BondScript.CreateLeaf(key.PubKey, UnlockHeight), Network);
	}

	/// <summary>A bond of 0.3 BTC that unlocks at the height of the tests, confirmed in block 976,172 if it is confirmed.</summary>
	private static FidelityBond CreateBond(bool confirmed)
	{
		using var key = new Key();
		var funding = Network.CreateTransaction();
		funding.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		funding.Outputs.Add(Money.Coins(0.3m), BondScript.GetScriptPubKey(BondScript.CreateLeaf(key.PubKey, UnlockHeight)));

		var transaction = new SmartTransaction(funding, confirmed ? new Height.ChainHeight(976_172) : Height.Mempool);
		return new FidelityBond(new OutPoint(funding, 0), Money.Coins(0.3m), UnlockHeight, transaction, SpenderTransaction: null);
	}
}
