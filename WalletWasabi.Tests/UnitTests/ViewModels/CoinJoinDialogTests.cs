using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.ViewModels.Wallets;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The dialog that the Coinjoin button of the Coins tab opens: one box whose menu lists what there is to choose from,
/// and above it the main screen of what is picked, the payment on the one of Payjoin. These are its rules that can be
/// told without a window: what the payment says about its amounts and its fee rate, what the menu lists, what the box
/// shows and what the screens are for, and which coins Play gives to a round.
/// </summary>
public class CoinJoinDialogTests
{
	[Fact]
	public void PaymentStartsWithTheFeeRateOfTheTitleBar()
	{
		var estimations = new FeeRateEstimations(new Dictionary<int, FeeRate>
		{
			[2] = new(102m),
			[3] = new(20m),
			[6] = new(10m)
		});

		Assert.Equal(new FeeRate(102m), CoinJoinViewModel.GetSuggestedFeeRate(estimations));
		Assert.Equal("102", CoinJoinViewModel.FormatFeeRate(CoinJoinViewModel.GetSuggestedFeeRate(estimations)!));

		// Without an estimate for the next two blocks the form suggests nothing, and neither does the title bar tell a fee rate.
		Assert.Null(CoinJoinViewModel.GetSuggestedFeeRate(new FeeRateEstimations(new Dictionary<int, FeeRate> { [6] = new(10m) })));
		Assert.Null(CoinJoinViewModel.GetSuggestedFeeRate(null));
	}

	[Theory]
	[InlineData("1", 1)]
	[InlineData("12.5", 12.5)]
	[InlineData(" 144.71 ", 144.71)]
	[InlineData("0.1", 0.1)]
	public void PaymentTakesAFeeRateInSatoshisPerVirtualByte(string text, decimal expected)
	{
		Assert.Equal(new FeeRate(expected), CoinJoinViewModel.ParseFeeRate(text));
	}

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("-1")]
	[InlineData("0")]
	[InlineData("0.01")]
	[InlineData("1,5")]
	[InlineData("1e3")]
	[InlineData("100000")]
	public void PaymentRefusesWhatIsNoFeeRate(string text)
	{
		Assert.Null(CoinJoinViewModel.ParseFeeRate(text));
	}

	[Fact]
	public void FeeRateIsWrittenWithoutNeedlessDigits()
	{
		Assert.Equal("144.71", CoinJoinViewModel.FormatFeeRate(new FeeRate(144.714m)));
		Assert.Equal("2", CoinJoinViewModel.FormatFeeRate(new FeeRate(2m)));
		Assert.Equal("0.1", CoinJoinViewModel.FormatFeeRate(new FeeRate(0.1m)));
	}

	[Fact]
	public void AmountIsCheckedAgainstTheSelectedCoins()
	{
		// All is well within what the coins hold, and at exactly that too: the payment takes its fee from itself then.
		Assert.Null(CoinJoinViewModel.DescribeAmountError(amountBtc: 0.05m, totalBtc: 0.05m, selectedBtc: 0.1m));
		Assert.Null(CoinJoinViewModel.DescribeAmountError(amountBtc: 0.1m, totalBtc: 0.1m, selectedBtc: 0.1m));

		// More than the coins hold, by this payment alone or by all the payments together.
		Assert.Equal("The selected coins hold less than that.", CoinJoinViewModel.DescribeAmountError(amountBtc: 0.2m, totalBtc: 0.2m, selectedBtc: 0.1m));
		Assert.Equal("The selected coins hold less than that.", CoinJoinViewModel.DescribeAmountError(amountBtc: 0.05m, totalBtc: 0.15m, selectedBtc: 0.1m));
	}

	[Fact]
	public void AmountIsNotHeldAgainstCoinsThatAreNotSelectedYet()
	{
		// The form may be filled in before the coins are picked. It cannot be continued without them.
		Assert.Null(CoinJoinViewModel.DescribeAmountError(amountBtc: 5m, totalBtc: 5m, selectedBtc: 0m));
	}

	[Fact]
	public void AmountOfNothingIsNotAnError()
	{
		// Nothing typed yet, a number that starts with a zero being typed, or what "Max" leaves once the last coin was
		// unticked: no message. The form cannot be continued with it.
		Assert.Null(CoinJoinViewModel.DescribeAmountError(amountBtc: 0m, totalBtc: 0m, selectedBtc: 1m));
		Assert.Null(CoinJoinViewModel.DescribeAmountError(amountBtc: 0m, totalBtc: 0m, selectedBtc: 0m));

		// The other payments take more than the coins hold, and "Max" leaves nothing for this one: their rows say so, this one does not.
		Assert.Null(CoinJoinViewModel.DescribeAmountError(amountBtc: 0m, totalBtc: 0.5m, selectedBtc: 0.1m));
	}

	[Fact]
	public void AmountCannotBeMoreThanThereIs()
	{
		Assert.Equal("Amount must be less than the total supply of BTC.", CoinJoinViewModel.DescribeAmountError(amountBtc: 21_000_001m, totalBtc: 21_000_001m, selectedBtc: 1m));
	}

	[Fact]
	public void FeeIsSaidToComeOutOfAPaymentThatTakesAll()
	{
		Assert.True(CoinJoinViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: 0.1m, hasAmountMessage: false));

		// The amount was typed: the fee is paid on top of it.
		Assert.False(CoinJoinViewModel.ShowsFeeSubtractionNote(takesAll: false, amountBtc: 0.1m, hasAmountMessage: false));

		// "Max" with the last coin unticked: there is no payment to take a fee from.
		Assert.False(CoinJoinViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: 0m, hasAmountMessage: false));
		Assert.False(CoinJoinViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: null, hasAmountMessage: false));

		// A message about the amount stands in the same place and goes first.
		Assert.False(CoinJoinViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: 0.1m, hasAmountMessage: true));
	}

	[Fact]
	public void HintTellsWhatToDoBeforeAnAmountCanBeSent()
	{
		// The coins are ticked on the Coins tab, which the dialog covers.
		Assert.Equal("Tick the coins to spend on the Coins tab", CoinJoinViewModel.DescribeSelectionHint(hasCoins: true));
		Assert.Equal("The wallet has no coins to spend", CoinJoinViewModel.DescribeSelectionHint(hasCoins: false));
	}

	/// <summary>
	/// The menu of the box lists what there is to choose from, Payjoin first. The dialog opens with Payjoin, whose main
	/// screen has the payment and Receive.
	/// </summary>
	[Fact]
	public void MenuListsWhatThereIsToChooseFromWithPayjoinFirst()
	{
		Assert.Equal(
			["Payjoin", "WabiSabi", "ZeroLink", "Taker"],
			CoinJoinBoxViewModel.AllOptions.Select(CoinJoinBoxViewModel.GetName));

		Assert.Equal(CoinJoinOption.Payjoin, CoinJoinBoxViewModel.DefaultOption);

		// Every one is listed once, and none is left out.
		Assert.Equal(Enum.GetValues<CoinJoinOption>().Order(), CoinJoinBoxViewModel.AllOptions.Order());
	}

	[Fact]
	public void KindsOfTheCoordinatorStartItsRounds()
	{
		Assert.Equal(CoinJoinProtocol.WabiSabi, CoinJoinBoxViewModel.GetProtocol(CoinJoinOption.WabiSabi));
		Assert.Equal(CoinJoinProtocol.ZeroLink, CoinJoinBoxViewModel.GetProtocol(CoinJoinOption.ZeroLink));

		// Every kind of round that the wallet can start has its entry in the menu.
		Assert.Equal(
			Enum.GetValues<CoinJoinProtocol>().Order(),
			CoinJoinBoxViewModel.AllOptions.Select(CoinJoinBoxViewModel.GetProtocol).OfType<CoinJoinProtocol>().Order());
	}

	/// <summary>
	/// Payjoin holds its place: there is nothing to play. The box says that the payment and Receive of its main screen
	/// work all the same.
	/// </summary>
	[Fact]
	public void PayjoinHoldsItsPlaceWithSendAndReceive()
	{
		Assert.Null(CoinJoinBoxViewModel.GetProtocol(CoinJoinOption.Payjoin));

		foreach (var isCoordinatorSet in new[] { true, false })
		{
			Assert.Equal(
				("Not available yet", "Send and Receive work without it", "Payjoin is not available yet."),
				CoinJoinBoxViewModel.DescribeUnavailable(CoinJoinOption.Payjoin, isCoordinatorSet));
		}

		Assert.Equal(
			"A payment spends the coins that are ticked on the Coins tab. Payjoin is not available yet: Send and Receive work the regular way.",
			CoinJoinViewModel.DescribeScreen(CoinJoinOption.Payjoin));
	}

	[Fact]
	public void TakerHoldsItsPlaceWithoutACoinjoinToStart()
	{
		Assert.Null(CoinJoinBoxViewModel.GetProtocol(CoinJoinOption.Taker));

		// It says so whether a coordinator is set or not: a taker coinjoins with market makers.
		foreach (var isCoordinatorSet in new[] { true, false })
		{
			var (notice, detail, noPlayToolTip) = CoinJoinBoxViewModel.DescribeUnavailable(CoinJoinOption.Taker, isCoordinatorSet);

			Assert.Equal("Not available yet", notice);
			Assert.Equal("", detail);
			Assert.Equal("Taker coinjoins are not available yet.", noPlayToolTip);
		}

		Assert.Contains("not available yet", CoinJoinViewModel.DescribeScreen(CoinJoinOption.Taker));
	}

	[Theory]
	[InlineData(CoinJoinOption.WabiSabi)]
	[InlineData(CoinJoinOption.ZeroLink)]
	public void RoundsOfTheCoordinatorNeedACoordinatorAndTheLoadedWallet(CoinJoinOption option)
	{
		// The settings of the coordinator are behind the gear of the title bar: the menu of the box has no way there.
		Assert.Equal(
			("Coordinator not configured", "Coinjoin not available", "Set a coordinator first: the gear in the top right corner, then Coordinator."),
			CoinJoinBoxViewModel.DescribeUnavailable(option, isCoordinatorSet: false));

		// With a coordinator the controls are only missing while the wallet still processes the blocks of the node.
		Assert.Equal(
			("Coinjoin waits for the wallet", "It is processing the blocks", "The wallet is still processing the blocks of the node."),
			CoinJoinBoxViewModel.DescribeUnavailable(option, isCoordinatorSet: true));
	}

	/// <summary>Every entry of the menu that can be picked has a main screen, and the dialog says what it is for.</summary>
	[Fact]
	public void EveryMainScreenSaysWhatItIsFor()
	{
		var captions = CoinJoinBoxViewModel.AllOptions.Select(CoinJoinViewModel.DescribeScreen).ToArray();

		Assert.All(captions, caption => Assert.False(string.IsNullOrWhiteSpace(caption)));
		Assert.Equal(captions.Length, captions.Distinct().Count());

		// The payments that wait for a round are on the main screen of WabiSabi.
		Assert.Contains("payments that are queued here", CoinJoinViewModel.DescribeScreen(CoinJoinOption.WabiSabi));
		Assert.Contains("one coin", CoinJoinViewModel.DescribeScreen(CoinJoinOption.ZeroLink));
	}

	[Fact]
	public void BoxShowsWhatIsPickedInTheMenu()
	{
		foreach (var option in CoinJoinBoxViewModel.AllOptions)
		{
			Assert.Equal(option, CoinJoinBoxViewModel.ChooseShownOption(selected: option, underWay: null));
		}
	}

	/// <summary>
	/// The wallet has one coinjoin at a time, and the box is where it is watched and stopped. So a coinjoin that is under
	/// way has the box, whatever is picked in the menu: the pick decides which main screen the dialog shows, so that a
	/// payment can be made, or an address be had, while a round goes on.
	/// </summary>
	[Fact]
	public void CoinjoinThatIsUnderWayHasTheBox()
	{
		foreach (var selected in CoinJoinBoxViewModel.AllOptions)
		{
			Assert.Equal(CoinJoinOption.WabiSabi, CoinJoinBoxViewModel.ChooseShownOption(selected, underWay: CoinJoinOption.WabiSabi));
			Assert.Equal(CoinJoinOption.ZeroLink, CoinJoinBoxViewModel.ChooseShownOption(selected, underWay: CoinJoinOption.ZeroLink));
		}
	}

	[Fact]
	public void EntryOfTheMenuIsCalledLikeWhatItPicks()
	{
		var picked = new List<CoinJoinOption>();

		foreach (var option in CoinJoinBoxViewModel.AllOptions)
		{
			using var pick = ReactiveUI.ReactiveCommand.Create(() => picked.Add(option));
			var entry = new CoinJoinOptionViewModel(option, pick);

			Assert.Equal(option, entry.Option);
			Assert.Equal(CoinJoinBoxViewModel.GetName(option), entry.Name);
			Assert.False(entry.IsSelected);

			entry.SelectCommand.Execute(null);
		}

		Assert.Equal(CoinJoinBoxViewModel.AllOptions, picked);
	}

	/// <summary>
	/// Play tells the wallet about every coin of the list, the unticked ones too: a coin that was chosen for an earlier
	/// round and is not ticked now has to be taken out again.
	/// </summary>
	[Fact]
	public void PlayNamesEveryCoinOfTheListWithItsTick()
	{
		var ticked = new OutPoint(uint256.One, 0);
		var unticked = new OutPoint(uint256.One, 1);

		var selection = CoinJoinBoxViewModel.GetRoundSelection([(ticked, true), (unticked, false)]);

		Assert.Equal(2, selection.Count);
		Assert.True(selection[ticked]);
		Assert.False(selection[unticked]);

		Assert.Empty(CoinJoinBoxViewModel.GetRoundSelection([]));
	}
}
