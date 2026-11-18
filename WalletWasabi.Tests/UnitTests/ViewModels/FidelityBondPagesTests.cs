using System.Linq;
using System.Numerics;
using NBitcoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Bonds;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// What the pages of the fidelity bonds tell about a bond, which months they offer and which fee rates they take.
/// </summary>
public class FidelityBondPagesTests
{
	private static readonly Network Network = Network.RegTest;
	private static readonly BondLocktime Locktime = BondLocktime.FromMonth(2027, 1);

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

	[Fact]
	public void DurationDoesNotDependOnTheTimeZone()
	{
		var from = new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.FromHours(-5)); // December 1, 04:00 UTC
		var to = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

		Assert.Equal("30 days", DurationText.Between(from, to));
	}

	[Fact]
	public void LockIsDescribedWithItsDateAndItsLength()
	{
		Assert.Equal(
			"Locked until January 1, 2027, 00:00 UTC. That is 1 month and 15 days from now.",
			CreateBondViewModel.DescribeLock(Locktime, DateTimeOffset.Parse("2026-11-17T00:00:00Z")));
	}

	[Fact]
	public void MonthsToLockUntilStartAfterNowAndGoOnForTenYears()
	{
		var clock = DateTimeOffset.Parse("2026-11-17T22:00:00Z");

		var months = WalletBondsModel.GetSelectableLocktimes(clock, chainTime: null);
		Assert.Equal(WalletBondsModel.MaxLockMonths, months.Count);
		Assert.Equal(BondLocktime.FromMonth(2026, 12), months[0]);
		Assert.Equal(BondLocktime.FromMonth(2036, 11), months[^1]);
		Assert.Equal(months.Order(), months);

		// The chain is a bit behind the clock, as it always is: the clock counts.
		Assert.Equal(months, WalletBondsModel.GetSelectableLocktimes(clock, clock.AddHours(-1)));

		// A month that the chain is past already is not offered, whatever the clock says.
		var ahead = WalletBondsModel.GetSelectableLocktimes(clock, DateTimeOffset.Parse("2026-12-02T00:00:00Z"));
		Assert.Equal(BondLocktime.FromMonth(2027, 1), ahead[0]);

		// The month that starts right now is not one to lock until.
		var atTheStart = WalletBondsModel.GetSelectableLocktimes(DateTimeOffset.Parse("2026-12-01T00:00:00Z"), chainTime: null);
		Assert.Equal(BondLocktime.FromMonth(2027, 1), atTheStart[0]);
	}

	[Fact]
	public void MonthsToLockUntilEndWhereTheBondsEnd()
	{
		var months = WalletBondsModel.GetSelectableLocktimes(DateTimeOffset.Parse("2099-06-15T00:00:00Z"), chainTime: null);
		Assert.Equal(6, months.Count);
		Assert.Equal(BondLocktime.FromMonth(2099, 7), months[0]);
		Assert.Equal(BondLocktime.Last, months[^1]);

		Assert.Empty(WalletBondsModel.GetSelectableLocktimes(DateTimeOffset.Parse("2099-12-01T00:00:00Z"), chainTime: null));
		Assert.Empty(WalletBondsModel.GetSelectableLocktimes(DateTimeOffset.Parse("2105-01-01T00:00:00Z"), chainTime: null));
	}

	[Fact]
	public void StateOfABondFollowsItsTransactionsAndTheTimeOfTheChain()
	{
		var before = Locktime.Date.AddDays(-10);
		var after = Locktime.Date.AddSeconds(1);

		var unconfirmed = CreateBond(confirmed: false);
		Assert.Equal(FidelityBondState.Unconfirmed, WalletBondsModel.GetState(unconfirmed, before));
		Assert.Equal(FidelityBondState.Unconfirmed, WalletBondsModel.GetState(unconfirmed, after));

		var confirmed = CreateBond(confirmed: true);
		Assert.Equal(FidelityBondState.Locked, WalletBondsModel.GetState(confirmed, before));
		Assert.Equal(FidelityBondState.Locked, WalletBondsModel.GetState(confirmed, Locktime.Date));
		Assert.Equal(FidelityBondState.Unlocked, WalletBondsModel.GetState(confirmed, after));

		// Without the time of the chain there is no telling, and then the bond is not offered for redemption.
		Assert.Equal(FidelityBondState.Locked, WalletBondsModel.GetState(confirmed, chainTime: null));

		var redeeming = confirmed with { SpenderTransaction = new SmartTransaction(Network.CreateTransaction(), Height.Mempool) };
		Assert.Equal(FidelityBondState.Redeeming, WalletBondsModel.GetState(redeeming, after));

		var redeemed = confirmed with { SpenderTransaction = new SmartTransaction(Network.CreateTransaction(), new Height.ChainHeight(200)) };
		Assert.Equal(FidelityBondState.Redeemed, WalletBondsModel.GetState(redeemed, after));
		Assert.Equal(FidelityBondState.Redeemed, WalletBondsModel.GetState(redeemed, chainTime: null));
	}

	[Fact]
	public void BondInTheListTellsItsStateItsDateAndItsValue()
	{
		var chainTime = DateTimeOffset.Parse("2026-11-17T00:00:00Z");

		var unconfirmed = CreateItem(FidelityBondState.Unconfirmed, value: null, chainTime);
		Assert.Equal("Waiting for confirmation", unconfirmed.StateText);
		Assert.Equal("Unlocks on January 1, 2027, 00:00 UTC", unconfirmed.DateText);
		Assert.Null(unconfirmed.ValueText);
		Assert.False(unconfirmed.IsRedeemable);
		Assert.False(unconfirmed.IsOver);

		var locked = CreateItem(FidelityBondState.Locked, new BigInteger(288_248), chainTime);
		Assert.Equal("Locked for 1 month and 15 days", locked.StateText);
		Assert.Equal("Unlocks on January 1, 2027, 00:00 UTC", locked.DateText);
		Assert.Equal("288,248", locked.ValueText);
		Assert.False(locked.IsRedeemable);

		// The node does not tell the time of the chain.
		Assert.Equal("Locked", CreateItem(FidelityBondState.Locked, value: null, chainTime: null).StateText);

		var unlocked = CreateItem(FidelityBondState.Unlocked, new BigInteger(259_451), Locktime.Date.AddDays(1));
		Assert.Equal("Unlocked", unlocked.StateText);
		Assert.Equal("Unlocked since January 1, 2027, 00:00 UTC", unlocked.DateText);
		Assert.True(unlocked.IsRedeemable);
		Assert.False(unlocked.IsOver);

		var redeeming = CreateItem(FidelityBondState.Redeeming, value: null, Locktime.Date.AddDays(1));
		Assert.Equal("Redemption waits for confirmation", redeeming.StateText);
		Assert.Equal("Was locked until January 1, 2027", redeeming.DateText);
		Assert.False(redeeming.IsRedeemable);
		Assert.True(redeeming.IsOver);

		var redeemed = CreateItem(FidelityBondState.Redeemed, value: null, Locktime.Date.AddDays(1));
		Assert.Equal("Redeemed", redeemed.StateText);
		Assert.True(redeemed.IsOver);
	}

	[Fact]
	public void ValueOfABondIsWrittenInFullHoweverLargeItIs()
	{
		var item = CreateItem(FidelityBondState.Locked, BigInteger.Parse("82963121322361847808"), chainTime: null);

		Assert.Equal("82,963,121,322,361,847,808", item.ValueText);
	}

	[Fact]
	public void RedeemButtonOfABondHandsTheBondOver()
	{
		FidelityBondModel? redeemed = null;
		var model = new FidelityBondModel(CreateBond(confirmed: true), CreateAddress(), FidelityBondState.Unlocked, Value: null);
		var item = new BondItemViewModel(model, Locktime.Date.AddDays(1), x => redeemed = x);

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

	private static BondItemViewModel CreateItem(FidelityBondState state, BigInteger? value, DateTimeOffset? chainTime) =>
		new(new FidelityBondModel(CreateBond(confirmed: state != FidelityBondState.Unconfirmed), CreateAddress(), state, value), chainTime, _ => { });

	private static BitcoinAddress CreateAddress()
	{
		using var key = new Key();
		return BondScript.GetAddress(BondScript.Create(key.PubKey, Locktime), Network);
	}

	private static FidelityBond CreateBond(bool confirmed)
	{
		using var key = new Key();
		var funding = Network.CreateTransaction();
		funding.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		funding.Outputs.Add(Money.Coins(0.3m), BondScript.GetScriptPubKey(BondScript.Create(key.PubKey, Locktime)));

		var transaction = new SmartTransaction(funding, confirmed ? new Height.ChainHeight(100) : Height.Mempool);
		return new FidelityBond(new OutPoint(funding, 0), Money.Coins(0.3m), Locktime, transaction, SpenderTransaction: null);
	}
}
