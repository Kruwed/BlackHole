using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Bonds;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Helpers;
using WalletWasabi.JoinMarket;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Offers;

/// <summary>Where the coins of an offer come from when it is posted.</summary>
public enum OfferCoinsSource
{
	/// <summary>No coin is ticked and no offer stands: there is nothing to offer.</summary>
	None,

	/// <summary>The coins that are ticked on the Coins tab.</summary>
	Ticked,

	/// <summary>No coin is ticked: the offer that stands keeps its coins, and only its terms change.</summary>
	StandingOffer,

	/// <summary>More coins are ticked than an offer holds.</summary>
	TooMany
}

/// <summary>
/// The offer of the wallet as a market maker: up to two coins, the lowest fee rate of a coinjoin that the coins may go
/// into and the two prices of an anonymity point, posted to the Nostr relays. The coins are the ones that are ticked on
/// the Coins tab. The offer that stands is shown here too, to be changed or taken back. It is the main screen of the
/// Maker tab of the Coinjoin dialog, and the fidelity bonds of the wallet are opened from here: a maker shows a bond.
/// </summary>
/// <remarks>
/// The screen is the same one for as long as the wallet is open, so the terms that were typed are still there when the
/// dialog is opened again.
/// </remarks>
[AppLifetime]
public partial class MakerOfferViewModel : ViewModelBase
{
	private readonly IWalletModel _walletModel;
	private readonly CoinListViewModel _coins;
	private readonly Action _close;

	[AutoNotify] private string _minFeeRateText = "";
	[AutoNotify] private string _bidText = "";
	[AutoNotify] private string _askText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private OfferCoinsSource _coinsSource;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _coinsText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private Amount? _offeredAmount;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasOffer;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _errorMessage;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isBusy;

	/// <param name="coins">The list of the Coins tab. An offer offers the coins that are ticked in it, and a bond locks them.</param>
	/// <param name="close">Closes the dialog. That is what follows an offer that was posted or taken back.</param>
	public MakerOfferViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, CoinListViewModel coins, Action close) : base(uiContext)
	{
		_walletModel = walletModel;
		_coins = coins;
		_close = close;

		// The terms of the offer that stands are what the screen starts with.
		var standing = walletModel.MakerOffer.Current;

		if (standing is not null)
		{
			_minFeeRateText = FormatFeeRate(standing.Offer.MinFeeRate);
			_bidText = FormatPrice(standing.Offer.Bid);
			_askText = FormatPrice(standing.Offer.Ask);
		}

		HasOffer = standing is not null;
		UpdateCoins();

		this.ValidateProperty(x => x.MinFeeRateText, ValidateMinFeeRate);
		this.ValidateProperty(x => x.BidText, ValidateBid);
		this.ValidateProperty(x => x.AskText, ValidateAsk);

		// The ask is measured against the bid, so what is said about it follows the bid as well.
		this.WhenAnyValue(x => x.BidText)
			.Skip(1)
			.Subscribe(_ => this.RaisePropertyChanged(nameof(AskText)));

		var canPost = this.WhenAnyValue(
			x => x.MinFeeRateText,
			x => x.BidText,
			x => x.AskText,
			x => x.CoinsSource,
			(feeRate, bid, ask, source) => ParseTerms(feeRate, bid, ask) is not null && source is OfferCoinsSource.Ticked or OfferCoinsSource.StandingOffer);

		var postCommand = ReactiveCommand.CreateFromTask(PostAsync, canPost);
		var withdrawCommand = ReactiveCommand.CreateFromTask(WithdrawAsync, this.WhenAnyValue(x => x.HasOffer));

		PostCommand = postCommand;
		WithdrawCommand = withdrawCommand;

		// The relays take a moment, through Tor more than one.
		postCommand.IsExecuting
			.CombineLatest(withdrawCommand.IsExecuting, (isPosting, isWithdrawing) => isPosting || isWithdrawing)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(isBusy => IsBusy = isBusy);

		// A maker shows a fidelity bond. A new bond locks the coins that are ticked.
		BondCommand = ReactiveCommand.Create(() => UiContext.Navigate().To().Bonds(walletModel, wallet, coins));
	}

	/// <summary>Posts the offer with the terms of the screen. The dialog closes when a relay took it.</summary>
	public ICommand PostCommand { get; }

	/// <summary>Takes the offer that stands back, and closes the dialog.</summary>
	public ICommand WithdrawCommand { get; }

	/// <summary>Opens the fidelity bonds of the wallet. The way back from there leads to the dialog.</summary>
	public ICommand BondCommand { get; }

	/// <summary>Whether the screen has no coins for an offer: none is ticked, or too many are. What it says about the coins is a complaint then.</summary>
	public bool HasNoCoinsToOffer => CoinsSource is OfferCoinsSource.None or OfferCoinsSource.TooMany;

	/// <summary>Where the offer goes, and what it tells there.</summary>
	public string PostingText
	{
		get
		{
			var relays = _walletModel.MakerOffer.Relays;
			var names = relays.Count == 0 ? "no relay" : string.Join(", ", relays.Select(relay => relay.Host));
			var way = _walletModel.MakerOffer.IsTorUsed ? "through Tor" : "directly, because Tor is turned off";

			return $"The offer is posted to Nostr relays ({names}), {way}. It tells the fee rate, the two prices and what the coins hold together, not which coins they are. It stands until you take it back or close Black Hole.";
		}
	}

	/// <summary>Reads a price of an anonymity point: a whole number of sats, with or without separators between the thousands.</summary>
	public static Money? ParsePrice(string text)
	{
		var digits = text.Trim().Replace(",", "").Replace(" ", "").Replace(" ", "");

		return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var sats) && sats <= Constants.MaximumNumberOfSatoshis
			? Money.Satoshis(sats)
			: null;
	}

	/// <summary>Reads the terms of an offer as the user types them. Null if they are not the terms of an offer.</summary>
	public static (FeeRate MinFeeRate, Money Bid, Money Ask)? ParseTerms(string minFeeRateText, string bidText, string askText) =>
		RedeemBondViewModel.ParseFeeRate(minFeeRateText.Trim()) is { } minFeeRate
		&& ParsePrice(bidText) is { } bid
		&& ParsePrice(askText) is { } ask
		&& MakerOffer.ValidateTerms(minFeeRate, bid, ask) is null
			? (minFeeRate, bid, ask)
			: null;

	public static string FormatFeeRate(FeeRate feeRate) => feeRate.SatoshiPerByte.ToString("0.###", CultureInfo.InvariantCulture);

	public static string FormatPrice(Money price) => price.Satoshi.ToString(CultureInfo.InvariantCulture);

	/// <summary>
	/// Tells where the coins of an offer come from that is posted now. The ticked coins are the offer's, if an offer
	/// can hold them. With no coin ticked, an offer that stands keeps its coins, so that its terms can be changed
	/// without ticking its coins again.
	/// </summary>
	/// <param name="tickedCount">How many coins are ticked on the Coins tab.</param>
	/// <param name="standingCount">How many coins the offer that stands holds: none without such an offer.</param>
	public static OfferCoinsSource ChooseCoins(int tickedCount, int standingCount) =>
		tickedCount switch
		{
			> MakerOffer.MaxCoinCount => OfferCoinsSource.TooMany,
			> 0 => OfferCoinsSource.Ticked,
			_ when standingCount > 0 => OfferCoinsSource.StandingOffer,
			_ => OfferCoinsSource.None
		};

	/// <summary>What the screen says about the coins of the offer: which ones they are, or what is to be done first.</summary>
	/// <param name="tickedCount">How many coins are ticked on the Coins tab.</param>
	/// <param name="standingCount">How many coins the offer that stands holds: none without such an offer.</param>
	public static string DescribeCoins(int tickedCount, int standingCount) =>
		ChooseCoins(tickedCount, standingCount) switch
		{
			OfferCoinsSource.TooMany =>
				$"An offer holds two coins at most, and {tickedCount} are ticked on the Coins tab. Coins on the same address are ticked together.",
			OfferCoinsSource.Ticked when standingCount > 0 =>
				$"The offer gets the {Count(tickedCount, "coin that is", "coins that are")} ticked on the Coins tab, in place of what it holds now.",
			OfferCoinsSource.Ticked =>
				$"The offer holds the {Count(tickedCount, "coin that is", "coins that are")} ticked on the Coins tab.",
			OfferCoinsSource.StandingOffer =>
				$"No coin is ticked on the Coins tab, so the offer keeps the {Count(standingCount, "coin", "coins")} that it holds now.",
			_ => "No coin is ticked. Close this dialog, tick one or two coins on the Coins tab and come back."
		};

	private static string Count(int count, string one, string many) => count == 1 ? one : $"{count} {many}";

	/// <summary>
	/// Keeps what the screen says about the coins and the offer up to date until what is given is disposed of: for as
	/// long as the dialog shows.
	/// </summary>
	public void Activate(CompositeDisposable disposables)
	{
		// Nothing is ticked or unticked while the dialog covers the Coins tab, but a coin can be spent meanwhile, and
		// the offer follows the coins that it holds.
		_coins.Selection
			.ToObservableChangeSet()
			.ToSignal()
			.Merge(_coins.CoinItems.ToObservableChangeSet().ToSignal())
			.Merge(_walletModel.MakerOffer.Changed.ToSignal())
			.StartWith(System.Reactive.Unit.Default)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ =>
			{
				HasOffer = _walletModel.MakerOffer.Current is not null;
				UpdateCoins();
			})
			.DisposeWith(disposables);
	}

	/// <summary>The coins that the offer that stands holds, as far as the wallet still has them.</summary>
	private SmartCoin[] GetStandingCoins() =>
		_walletModel.MakerOffer.Current is { } standing
			? [.. _coins.CoinItems.Select(row => row.Coin.GetSmartCoin()).Where(coin => standing.Coins.Contains(coin.Outpoint))]
			: [];

	/// <summary>The coins that an offer gets that is posted now. None if there is nothing to offer, or too much.</summary>
	private SmartCoin[] GetCoinsToOffer()
	{
		var ticked = _coins.Selection.GetSmartCoins().ToArray();
		var standing = GetStandingCoins();

		return ChooseCoins(ticked.Length, standing.Length) switch
		{
			OfferCoinsSource.Ticked => ticked,
			OfferCoinsSource.StandingOffer => standing,
			_ => []
		};
	}

	private void UpdateCoins()
	{
		var tickedCount = _coins.Selection.Count;
		var standingCount = GetStandingCoins().Length;
		var coins = GetCoinsToOffer();

		CoinsSource = ChooseCoins(tickedCount, standingCount);
		CoinsText = DescribeCoins(tickedCount, standingCount);
		OfferedAmount = coins.Length > 0 ? new Amount(coins.Sum(coin => coin.Amount)) : null;
		this.RaisePropertyChanged(nameof(HasNoCoinsToOffer));
	}

	private async Task PostAsync()
	{
		if (ParseTerms(MinFeeRateText, BidText, AskText) is not { } terms)
		{
			return;
		}

		// The coins are taken now: a coin that was spent while the dialog was open is not among them.
		var coins = GetCoinsToOffer();

		if (coins.Length == 0)
		{
			UpdateCoins();
			return;
		}

		ErrorMessage = null;

		try
		{
			// The offer is not called back when the dialog is left meanwhile: what reached a relay is on the market.
			var answers = await _walletModel.MakerOffer.PostAsync(coins, terms.MinFeeRate, terms.Bid, terms.Ask, CancellationToken.None);

			if (answers.Any(answer => answer.Accepted))
			{
				_close();
			}
			else
			{
				ErrorMessage = $"The offer was not posted. {MakerOfferService.DescribeFailure(answers)}";
			}
		}
		catch (ArgumentException ex)
		{
			// A coin was spent while the dialog was open, for instance.
			ErrorMessage = ex.Message;
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			ErrorMessage = $"The offer was not posted. {ex.Message}";
		}
	}

	private async Task WithdrawAsync()
	{
		ErrorMessage = null;

		try
		{
			await _walletModel.MakerOffer.WithdrawAsync(CancellationToken.None);
			_close();
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			ErrorMessage = ex.Message;
		}
	}

	private void ValidateMinFeeRate(IValidationErrors errors)
	{
		if (MinFeeRateText.Trim() == "" || RedeemBondViewModel.ParseFeeRate(MinFeeRateText.Trim()) is not null)
		{
			return;
		}

		errors.Add(
			ErrorSeverity.Error,
			$"Between {FormatFeeRate(Constants.MinRelayFeeRate)} and {FormatFeeRate(Constants.AbsurdlyHighFeeRate)} sat/vB.");
	}

	private void ValidateBid(IValidationErrors errors)
	{
		if (BidText.Trim() != "" && ParsePrice(BidText) is null)
		{
			errors.Add(ErrorSeverity.Error, "A whole number of sats.");
		}
	}

	private void ValidateAsk(IValidationErrors errors)
	{
		if (AskText.Trim() == "")
		{
			return;
		}

		if (ParsePrice(AskText) is not { } ask)
		{
			errors.Add(ErrorSeverity.Error, "A whole number of sats.");
		}
		else if (ParsePrice(BidText) is { } bid && ask < bid)
		{
			errors.Add(ErrorSeverity.Error, "Not lower than the bid.");
		}
	}
}
