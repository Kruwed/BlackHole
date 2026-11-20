using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Bonds;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Helpers;
using WalletWasabi.JoinMarket;
using WalletWasabi.Logging;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Offers;

/// <summary>
/// The offer of the wallet as a market maker: the user picks up to two coins, names the lowest fee rate of a coinjoin
/// that the coins may go into and the two prices of an anonymity point, and posts that to the Nostr relays. The offer
/// that stands is shown here too, to be changed or taken back.
/// </summary>
[NavigationMetaData(Title = "Offer", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class MakerOfferViewModel : RoutableViewModel
{
	private readonly IWalletModel _walletModel;

	[AutoNotify] private string _minFeeRateText = "";
	[AutoNotify] private string _bidText = "";
	[AutoNotify] private string _askText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private int _selectedCount;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private Amount? _selectedAmount;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasOffer;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _errorMessage;

	public MakerOfferViewModel(UiContext uiContext, IWalletModel walletModel) : base(uiContext)
	{
		_walletModel = walletModel;

		// The offer that stands is what the page starts with.
		var standing = walletModel.MakerOffer.Current;
		var offeredCoins = standing is null
			? []
			: walletModel.Coins.List.Items.Where(coin => standing.Coins.Contains(coin.GetSmartCoin().Outpoint)).ToList();

		if (standing is not null)
		{
			_minFeeRateText = FormatFeeRate(standing.Offer.MinFeeRate);
			_bidText = FormatPrice(standing.Offer.Bid);
			_askText = FormatPrice(standing.Offer.Ask);
		}

		CoinList = new CoinListViewModel(uiContext, walletModel.Coins, offeredCoins, allowCoinjoiningCoinSelection: true, allowSelection: true);
		HasOffer = standing is not null;

		// A click beside the page does not close it: what was typed would be gone.
		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: false);

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
			x => x.SelectedCount,
			(feeRate, bid, ask, count) => ParseTerms(feeRate, bid, ask) is not null && count is > 0 and <= MakerOffer.MaxCoinCount);

		NextCommand = ReactiveCommand.CreateFromTask(PostAsync, canPost);
		SkipCommand = ReactiveCommand.CreateFromTask(WithdrawAsync, this.WhenAnyValue(x => x.HasOffer));

		EnableAutoBusyOn(NextCommand, SkipCommand);
	}

	public CoinListViewModel CoinList { get; }

	/// <summary>Whether more coins are ticked than an offer holds.</summary>
	public bool HasTooManyCoins => SelectedCount > MakerOffer.MaxCoinCount;

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

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// What is ticked, also when the list is put together again because the coins of the wallet changed.
		CoinList.CoinItems
			.ToObservableChangeSet()
			.AutoRefresh(x => x.IsSelected)
			.ToCollection()
			.Subscribe(UpdateSelection)
			.DisposeWith(disposables);

		_walletModel.MakerOffer.Changed
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(offer => HasOffer = offer is not null)
			.DisposeWith(disposables);

		HasOffer = _walletModel.MakerOffer.Current is not null;
	}

	protected override void OnNavigatedFrom(bool isInHistory)
	{
		if (!isInHistory)
		{
			CoinList.Dispose();
		}
	}

	/// <summary>
	/// Follows the ticks. Once an offer is full, the coins that are not in it cannot be ticked, which shows how many
	/// coins an offer holds. Coins on one address are ticked together, so more than that can be ticked all the same.
	/// </summary>
	private void UpdateSelection(IReadOnlyCollection<CoinViewModel> rows)
	{
		var selected = rows.Where(row => row.IsSelected).ToArray();
		var isFull = selected.Length >= MakerOffer.MaxCoinCount;

		foreach (var row in rows)
		{
			row.CanBeSelected = row.IsSelected || !isFull;
		}

		SelectedCount = selected.Length;
		SelectedAmount = selected.Length > 0 ? new Amount(selected.Sum(row => row.Coin.Amount)) : null;
		this.RaisePropertyChanged(nameof(HasTooManyCoins));
	}

	private async Task PostAsync()
	{
		if (ParseTerms(MinFeeRateText, BidText, AskText) is not { } terms)
		{
			return;
		}

		ErrorMessage = null;
		var coins = CoinList.Selection.GetSmartCoins().ToArray();

		try
		{
			// The offer is not called back when the page is left meanwhile: what reached a relay is on the market.
			var answers = await _walletModel.MakerOffer.PostAsync(coins, terms.MinFeeRate, terms.Bid, terms.Ask, CancellationToken.None);

			if (answers.Any(answer => answer.Accepted))
			{
				Navigate().Clear();
			}
			else
			{
				ErrorMessage = $"The offer was not posted. {MakerOfferService.DescribeFailure(answers)}";
			}
		}
		catch (ArgumentException ex)
		{
			// A coin was spent while the page was open, for instance.
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
			Navigate().Clear();
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
