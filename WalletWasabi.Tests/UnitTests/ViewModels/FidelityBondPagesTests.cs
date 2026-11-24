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
/// What the wallet tells about a fidelity bond, which unlock heights and fee rates it takes, and how the history and
/// the notifications name the transactions of a bond. Locking a bond is a part of the Maker tab, which has its own tests.
/// </summary>
public class FidelityBondPagesTests
{
	private const uint UnlockHeight = 1_028_732;
	private static readonly Network Network = Network.RegTest;

	[Fact]
	public void HeightIsWrittenWithSeparators()
	{
		Assert.Equal("1,028,732", BlockHeightText.Format(1_028_732));
		Assert.Equal("150", BlockHeightText.Format(150));
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
