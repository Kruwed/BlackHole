using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Market;

/// <summary>
/// The maker's side of the market, which is the main screen of the Maker tab of the Coinjoin dialog: the strategy
/// triangle with the split of the wallet and the rates of the offer on the left, the coins of the wallet in the
/// middle, and on the right the two things that a maker does with them, Lock Coins and List Offers.
/// </summary>
/// <remarks>
/// Everything that is worked out here is worked out by <see cref="MakerDesk"/>. What the screen does with coins it
/// does through the market that it was given: a bond is locked and an offer is posted there.
/// </remarks>
public partial class MakerScreenViewModel : ReactiveObject
{
	private readonly IMarket _market;
	private readonly Func<BondQuote, Task<bool>> _confirmBond;
	private readonly Func<Task<bool>> _authorize;
	private readonly BarRowViewModel _liquidityRow;
	private readonly BarRowViewModel _bondRow;
	private readonly Dictionary<BookCriterion, BarRowViewModel> _rateRows = [];

	private MakerDesk _desk;
	private MakerDeskState _state;
	private MakerDeskView _view;

	[AutoNotify] private TriangleWeights? _strategy;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _strategyText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<MakerCoinRowViewModel> _coins = [];
	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<OfferCardViewModel> _offers = [];
	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<BondCardViewModel> _bonds = [];
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isAllSelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSomeSelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isAnySelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isBusy;

	/// <param name="confirmBond">Asks the user whether to lock the bond that a question describes.</param>
	/// <param name="authorize">Asks the user for what the wallet needs to sign, if it needs anything.</param>
	public MakerScreenViewModel(IMarket market, Func<BondQuote, Task<bool>> confirmBond, Func<Task<bool>> authorize)
	{
		_market = market;
		_confirmBond = confirmBond;
		_authorize = authorize;
		_desk = CreateDesk();
		_state = _desk.Open();
		_view = _desk.GetView(_state);

		_liquidityRow = new BarRowViewModel(
			"Liquidity",
			moved: percent => Apply(_desk.SetBondShare(_state, 1 - percent / 100)),
			typed: text => Apply(_desk.SetAllocationAmount(_state, isBond: false, text)));

		_bondRow = new BarRowViewModel(
			"Bond",
			moved: percent => Apply(_desk.SetBondShare(_state, percent / 100)),
			typed: text => Apply(_desk.SetAllocationAmount(_state, isBond: true, text)));

		AllocationRows = [_liquidityRow, _bondRow];

		(BookCriterion Rate, string Label)[] rates = [(BookCriterion.FeeRate, "Fee rate"), (BookCriterion.SellRate, "Sell rate"), (BookCriterion.BuyRate, "Buy rate")];

		RateRows = [.. rates
			.Select(rate =>
			{
				var row = new BarRowViewModel(
					rate.Label,
					moved: percent => Apply(_desk.SetRatePosition(_state, rate.Rate, percent)),
					typed: text => Apply(_desk.SetRateText(_state, rate.Rate, text)));

				_rateRows[rate.Rate] = row;
				return row;
			})];

		ToggleCommand = ReactiveCommand.Create<string>(id => Apply(_desk.Toggle(_state, id)));
		ToggleAllCommand = ReactiveCommand.Create(() => Apply(_desk.ToggleAll(_state)));
		ToggleAddressesCommand = ReactiveCommand.Create(() => Apply(_desk.ToggleAddresses(_state)));
		SortByPointsCommand = ReactiveCommand.Create(() => Apply(_desk.SortBy(_state, CoinSortKey.Points)));
		SortByAmountCommand = ReactiveCommand.Create(() => Apply(_desk.SortBy(_state, CoinSortKey.Amount)));
		ShowBondCommand = ReactiveCommand.Create(() => Apply(_desk.SetTab(_state, MakerTab.Bond)));
		ShowOffersCommand = ReactiveCommand.Create(() => Apply(_desk.SetTab(_state, MakerTab.Offers)));

		var notBusy = this.WhenAnyValue(x => x.IsBusy).Select(isBusy => !isBusy);

		PostOfferCommand = ReactiveCommand.CreateFromTask(PostOfferAsync, notBusy);
		CancelOfferCommand = ReactiveCommand.CreateFromTask<int>(CancelOfferAsync, notBusy);
		BondCommand = ReactiveCommand.CreateFromTask(LockBondAsync, notBusy);
		RedeemCommand = ReactiveCommand.Create<MarketBondInfo>(bond => _market.RedeemBond(bond));

		// The triangle tells where its marker was put.
		this.WhenAnyValue(x => x.Strategy)
			.Skip(1)
			.Where(weights => weights is not null && weights != _state.Strategy)
			.Subscribe(weights => Apply(_desk.ApplyStrategy(_state, weights!.Value)));

		Refresh();
	}

	/// <summary>The split of the wallet: Liquidity and Bond.</summary>
	public IReadOnlyList<BarRowViewModel> AllocationRows { get; }

	/// <summary>The rates of the offer: Fee rate, Sell rate, Buy rate.</summary>
	public IReadOnlyList<BarRowViewModel> RateRows { get; }

	/// <summary>Picks a coin by its name, or lets it go.</summary>
	public ICommand ToggleCommand { get; }

	public ICommand ToggleAllCommand { get; }

	/// <summary>The list names the coins by their addresses, or by the outputs that they are.</summary>
	public ICommand ToggleAddressesCommand { get; }

	public ICommand SortByPointsCommand { get; }

	public ICommand SortByAmountCommand { get; }

	public ICommand ShowBondCommand { get; }

	public ICommand ShowOffersCommand { get; }

	public ICommand PostOfferCommand { get; }

	/// <summary>Takes an offer back, by its number.</summary>
	public ICommand CancelOfferCommand { get; }

	public ICommand BondCommand { get; }

	/// <summary>Opens what pays a bond that has unlocked back to the wallet.</summary>
	public ICommand RedeemCommand { get; }

	public bool IsSample => _market.IsSample;

	public string TopName => MakerStrategy.TopName;

	public string LeftName => MakerStrategy.LeftName;

	public string RightName => MakerStrategy.RightName;

	public bool IsBondTab => _state.Tab == MakerTab.Bond;

	public bool IsOffersTab => _state.Tab == MakerTab.Offers;

	/// <summary>Whether coins of the wallet are locked in a bond. The Lock Coins tab tells the bond then, in place of the form.</summary>
	public bool HasBond => _desk.HasBond;

	public bool IsAllocationEnabled => _view.IsAllocationEnabled;

	public bool AreRatesEnabled => _view.AreRatesEnabled;

	/// <summary>What the second column of the list is called: the coins are named by their outputs or by their addresses.</summary>
	public string ReferenceHeader => _state.ShowsAddresses ? "Address" : "UTXO";

	public bool IsSortedByAmount => _state.SortKey == CoinSortKey.Amount;

	public bool IsSortedByPoints => _state.SortKey == CoinSortKey.Points;

	public bool IsSortAscending => _state.SortAscending;

	/// <summary>"Offer 4.3 AP".</summary>
	public string OfferButtonText => $"Offer {_view.OfferPoints} AP";

	public string? OfferError => _state.OfferError;

	public bool HasOfferError => _state.OfferError is not null;

	public string? BondError => _state.BondError;

	public bool HasBondError => _state.BondError is not null;

	public string OfferAmount
	{
		get => _view.OfferAmount;
		set => Type(_state with { OfferAmount = MakerDesk.CleanNumber(value, 8), OfferError = null }, _view.OfferAmount, value);
	}

	public string OfferFeeRate
	{
		get => _state.OfferFeeRate;
		set => Type(_state with { OfferFeeRate = MakerDesk.CleanNumber(value), OfferError = null }, _state.OfferFeeRate, value);
	}

	/// <summary>
	/// The sell rate of the offer. An offer of coins without anonymity points has none to sell: it lists 0, and the box
	/// cannot be typed into until a coin with points is picked.
	/// </summary>
	public string OfferSellRate
	{
		get => _view.OfferSellRate;
		set
		{
			if (!_view.IsSellRateFixed)
			{
				Type(_state with { OfferSellRate = MakerDesk.CleanNumber(value, 1), OfferError = null }, _state.OfferSellRate, value);
			}
		}
	}

	/// <summary>Whether the sell rate is the maker's to set: the picked coins have anonymity points to sell.</summary>
	public bool IsSellRateEnabled => !_view.IsSellRateFixed;

	/// <summary>Why the sell rate cannot be set, while it cannot.</summary>
	public string? SellRateNote => _view.IsSellRateFixed ? "The picked coins have no anonymity points to sell, so the offer lists a sell rate of 0." : null;

	public string OfferBuyRate
	{
		get => _state.OfferBuyRate;
		set => Type(_state with { OfferBuyRate = MakerDesk.CleanNumber(value, 1), OfferError = null }, _state.OfferBuyRate, value);
	}

	public string BondAmount
	{
		get => _view.BondAmount;
		set => Type(_state with { BondAmount = MakerDesk.CleanNumber(value, 8), BondError = null }, _view.BondAmount, value);
	}

	public string BondBlocks
	{
		get => _state.BondBlocks;
		set => Type(_state with { BondBlocks = MakerDesk.CleanNumber(value, 0), BondError = null }, _state.BondBlocks, value);
	}

	public string BondFeeRate
	{
		get => _state.BondFeeRate;
		set => Type(_state with { BondFeeRate = MakerDesk.CleanNumber(value), BondError = null }, _state.BondFeeRate, value);
	}

	/// <summary>Follows the market for as long as the screen shows: the coins, the bonds, the offers and the book.</summary>
	public void Activate(CompositeDisposable disposables)
	{
		_market.Changed
			.Subscribe(_ => Rebase())
			.DisposeWith(disposables);

		Rebase();
	}

	/// <summary>Posts the offer of the form, if the form is complete. Otherwise the form says what is missing.</summary>
	public async Task PostOfferAsync()
	{
		var (draft, state) = _desk.ValidateOffer(_state);
		Apply(state);

		if (draft is null)
		{
			return;
		}

		IsBusy = true;

		try
		{
			// The offer is not called back when the screen is left meanwhile: what reached a relay is on the market.
			if (await _market.PostOfferAsync(draft, CancellationToken.None) is { } error)
			{
				Apply(_state with { OfferError = error });
				return;
			}

			_desk = CreateDesk();
			Apply(_desk.AfterOfferPosted(_state));
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			Apply(_state with { OfferError = ex.Message });
		}
		finally
		{
			IsBusy = false;
		}
	}

	public async Task CancelOfferAsync(int id)
	{
		IsBusy = true;

		try
		{
			await _market.CancelOfferAsync(id, CancellationToken.None);

			_desk = CreateDesk();
			Apply(_desk.AfterOfferCancelled(_state));
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			Apply(_state with { OfferError = ex.Message });
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>Locks the bond of the form, if the form is complete and the user confirms it. Otherwise the form says what is missing.</summary>
	public async Task LockBondAsync()
	{
		var (draft, state) = _desk.ValidateBond(_state);
		Apply(state);

		if (draft is null)
		{
			return;
		}

		IsBusy = true;

		try
		{
			var (quote, error) = await _market.QuoteBondAsync(draft, CancellationToken.None);

			if (quote is null)
			{
				Apply(_state with { BondError = error ?? "The bond cannot be locked." });
				return;
			}

			if (!await _confirmBond(quote))
			{
				return;
			}

			// An empty answer is the user giving up at the last step. That is no error to tell.
			if (await _market.LockBondAsync(quote, _authorize, CancellationToken.None) is { } lockError)
			{
				Apply(_state with { BondError = lockError.Length > 0 ? lockError : null });
				return;
			}

			_desk = CreateDesk();
			Apply(_desk.AfterBondLocked(_state));
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			Apply(_state with { BondError = ex.Message });
		}
		finally
		{
			IsBusy = false;
		}
	}

	private MakerDesk CreateDesk()
	{
		var offers = _market.Offers;

		return new MakerDesk(
			_market.Book,
			[.. _market.Coins.Select(coin => coin.Coin)],
			offers.SelectMany(offer => offer.CoinIds).ToHashSet(),
			offers.Sum(offer => offer.Amount));
	}

	/// <summary>Takes the coins, the offers and the book as they are now, and carries over what was set.</summary>
	private void Rebase()
	{
		_desk = CreateDesk();
		Apply(_desk.Rebase(_state));
	}

	/// <summary>What was typed into a box of a form is the state of the form, cleaned of what no number has.</summary>
	private void Type(MakerDeskState state, string shown, string typed)
	{
		if (typed != shown)
		{
			Apply(state);
		}
	}

	private void Apply(MakerDeskState state)
	{
		_state = state;
		Refresh();
	}

	/// <summary>Shows what the state has to show.</summary>
	private void Refresh()
	{
		_view = _desk.GetView(_state);

		var infos = _market.Coins.ToDictionary(coin => coin.Coin.Id);

		// While the list has the same coins in the same order, its rows are told what changed and stay where they are:
		// a list that is made again starts at its top again, under the hand of whoever ticked a coin further down.
		if (Coins.Count == _view.Coins.Count && Coins.Zip(_view.Coins).All(pair => pair.First.Coin.Equals(pair.Second.Coin)))
		{
			foreach (var (shown, row) in Coins.Zip(_view.Coins))
			{
				shown.Update(row, infos.GetValueOrDefault(row.Coin.Id), _state.ShowsAddresses);
			}
		}
		else
		{
			Coins = [.. _view.Coins.Select(row => new MakerCoinRowViewModel(row, infos.GetValueOrDefault(row.Coin.Id), _state.ShowsAddresses, ToggleCommand))];
		}

		Offers = [.. _market.Offers.Select(offer => new OfferCardViewModel(offer, CancelOfferCommand))];
		Bonds = [.. _market.Bonds.Select(bond => new BondCardViewModel(bond, BondFigures.GetStanding(bond.Amount, _market.Book), RedeemCommand))];

		foreach (var row in _view.AllocationRows)
		{
			(row.Key == "bond" ? _bondRow : _liquidityRow).Show(row.Percent, 100, row.Caption, row.ValueText, row.InputText, _view.IsAllocationEnabled);
		}

		(BookCriterion Rate, string Key)[] rateKeys = [(BookCriterion.FeeRate, "minFee"), (BookCriterion.SellRate, "sell"), (BookCriterion.BuyRate, "buy")];

		foreach (var (rate, key) in rateKeys)
		{
			var row = _view.RateRows.Single(candidate => candidate.Key == key);

			// Without a book there is nothing to place a rate in, and an offer without points has no sell rate to place.
			var isFixed = rate == BookCriterion.SellRate && _view.IsSellRateFixed;

			_rateRows[rate].Show(row.Percent, 100, row.Caption, row.ValueText, row.InputText, _view.AreRatesEnabled && _desk.Book.Count > 0 && !isFixed);
		}

		IsAllSelected = _view.SelectAll == SelectAllState.All;
		IsSomeSelected = _view.SelectAll == SelectAllState.Some;
		IsAnySelected = _view.SelectAll != SelectAllState.None;

		Strategy = _state.Strategy;
		StrategyText = _state.Strategy is { } weights ? StrategyTriangle.Describe(weights, "AP", "Sats", "Remix") : "";

		string[] properties =
		[
			nameof(IsBondTab), nameof(IsOffersTab), nameof(HasBond), nameof(IsAllocationEnabled), nameof(AreRatesEnabled),
			nameof(ReferenceHeader), nameof(IsSortedByAmount), nameof(IsSortedByPoints), nameof(IsSortAscending),
			nameof(OfferButtonText), nameof(OfferError), nameof(HasOfferError), nameof(BondError), nameof(HasBondError),
			nameof(OfferAmount), nameof(OfferFeeRate), nameof(OfferSellRate), nameof(OfferBuyRate), nameof(IsSellRateEnabled), nameof(SellRateNote),
			nameof(BondAmount), nameof(BondBlocks), nameof(BondFeeRate)
		];

		foreach (var property in properties)
		{
			this.RaisePropertyChanged(property);
		}
	}
}
