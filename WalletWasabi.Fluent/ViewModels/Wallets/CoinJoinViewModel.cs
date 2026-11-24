using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Extensions;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Controls;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Bonds;
using WalletWasabi.Fluent.ViewModels.Wallets.CoinJoinPayment;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Fluent.ViewModels.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Services;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;
using Address = WalletWasabi.Userfacing.Address;
using Constants = WalletWasabi.Helpers.Constants;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

/// <summary>
/// The dialog that the Coinjoin button of the Coins tab opens. What is done with the coins that are ticked on that tab
/// starts here, and so does receiving. The dialog has a tab for every protocol, like the settings have theirs, and it
/// shows the main screen of the tab that is selected:
/// <list type="bullet">
/// <item>Payjoin, which the dialog opens with: the ordinary payment with its Send button, and Receive. Payjoin itself
/// holds its place and does nothing yet.</item>
/// <item>WabiSabi: the payments that wait for a round to make them.</item>
/// <item>ZeroLink: nothing but what the tab says about itself.</item>
/// <item>Taker: the order book of the market for anonymity points, with its filters, and the transaction that the
/// picked makers would make with the ticked coins.</item>
/// <item>Maker: the coins of the wallet, of which some are locked in a fidelity bond and others are offered to takers.
/// The coins of a bond or of an offer are picked on that tab itself.</item>
/// </list>
/// The box of the coinjoins is at the bottom of the dialog, below every tab.
/// </summary>
/// <remarks>
/// The dialog is the same one for as long as the wallet is open, so what was typed into the payment is still there
/// when the dialog is opened again, for instance after another coin was ticked. For a payment the dialog takes the
/// address, the amount and the fee rate. Nothing is signed here: "Send" shows the transaction, and it is sent from
/// there.
/// </remarks>
[AppLifetime]
[NavigationMetaData(Title = "Coinjoin", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class CoinJoinViewModel : RoutableViewModel
{
	/// <summary>The fee rate that the form starts with is the estimate for a confirmation within this many blocks, as in the title bar.</summary>
	private const int SuggestedFeeRateTarget = 2;

	/// <summary>How wide and how high the dialog may get while it shows a side of the market.</summary>
	public const double MarketDialogMaxWidth = 1680;

	public const double MarketDialogMaxHeight = 1300;

	private readonly Wallet _wallet;
	private readonly IWalletModel _walletModel;
	private readonly CoinListViewModel _coins;
	private readonly CoinJoinManager? _coinJoinManager;
	private readonly Subject<Unit> _recipientsChanged = new();
	private readonly ObservableCollection<RecipientRowViewModel> _additionalRecipients = [];

	private Address? _parsedAddress;
	private bool _isRecalculating;

	/// <summary>Whether the coinjoins of the wallet were told to wait for the transaction that the following pages show.</summary>
	private bool _isInSendWorkflow;

	/// <summary>What the fee rate box was last filled with from the estimates of the node. A text other than this one is the user's.</summary>
	private string _suggestedFeeRateText = "";

	[AutoNotify] private string _to = "";
	[AutoNotify] private decimal? _amountBtc;
	[AutoNotify] private string _feeRateText = "";
	[AutoNotify] private bool _isFixedAmount;
	[AutoNotify] private string? _bitcoinContent;
	[AutoNotify] private bool _isPayToMany;
	[AutoNotify] private bool _isPrimarySubtractFee;
	[AutoNotify] private bool _isBip21;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasSelection;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private Amount _selectedAmount = Amount.Zero;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _selectionHint = DescribeSelectionHint(hasCoins: false);
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isFeeSubtractionNoteVisible;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletLoaded;
	[AutoNotify] private int _selectedTab = GetTabIndex(CoinJoinBoxViewModel.DefaultTab);
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _description = DescribeScreen(CoinJoinBoxViewModel.DefaultTab);
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _caption = DescribeCoins(CoinJoinBoxViewModel.DefaultTab);
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isMarketTab;

	/// <param name="coins">The list of the Coins tab. The dialog works with the coins that are ticked in it.</param>
	public CoinJoinViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, CoinListViewModel coins) : base(uiContext)
	{
		_wallet = wallet;
		_walletModel = walletModel;
		_coins = coins;
		_coinJoinManager = UiContext.Services.GetHostedService<CoinJoinManager>();

		AdditionalRecipients = new IndexedCollection<RecipientRowViewModel>(_additionalRecipients);

		// What the wallet knows about its coins is not complete until it has processed the blocks of the node, so
		// everything that takes coins waits for that. Receiving does not.
		walletModel.WhenAnyValue(x => x.IsLoaded)
			.BindTo(this, x => x.IsWalletLoaded);

		// One box for all the tabs. A round gets the coins that are ticked in the list.
		Box = new CoinJoinBoxViewModel(uiContext, walletModel, coins);

		// The dialog shows the main screen of the tab that is selected, and the box is set to it.
		this.WhenAnyValue(x => x.SelectedTab)
			.Select(GetTab)
			.Subscribe(tab =>
			{
				Box.SelectedTab = tab;
				Description = DescribeScreen(tab);
				IsMarketTab = IsMarket(tab);

				// The two sides of the market need more room than a form does: the dialog grows for them.
				this.RaisePropertyChanged(nameof(DialogMaxWidth));
				this.RaisePropertyChanged(nameof(DialogMaxHeight));
			});

		// The payments that wait for a WabiSabi round are a part of the main screen of WabiSabi.
		Payments = new CoinJoinPaymentsViewModel(uiContext, walletModel, wallet);

		// The two sides of the market for anonymity points are the main screens of Taker and Maker.
		Market = new MarketViewModel(uiContext, walletModel, wallet, coins);

		// Under the title the dialog tells whose coins the tab works with: the ticked ones, the ones of the list on the
		// tab, or the examples of the design.
		this.WhenAnyValue(x => x.SelectedTab, x => x.Market.IsSample)
			.Subscribe(x => Caption = DescribeCoins(GetTab(x.Item1), x.Item2));

		// Nothing that was typed is lost by closing the dialog, so a click beside it may close it.
		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);

		this.ValidateProperty(x => x.To, ValidateToField);
		this.ValidateProperty(x => x.AmountBtc, ValidateAmount);
		this.ValidateProperty(x => x.FeeRateText, ValidateFeeRate);

		this.WhenAnyValue(x => x.To)
			.Skip(1)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(HandleAddressChange);

		// The fee of a payment that takes all is taken from it. An amount that the user types is theirs again.
		this.WhenAnyValue(x => x.AmountBtc)
			.Skip(1)
			.Where(_ => IsPrimarySubtractFee && !_isRecalculating)
			.Subscribe(_ => IsPrimarySubtractFee = false);

		// The note that the fee comes out of the payment stands where a message about the amount would stand.
		this.WhenAnyValue(x => x.IsPrimarySubtractFee, x => x.AmountBtc)
			.Subscribe(_ => UpdateFeeSubtractionNote());

		ErrorsChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(AmountBtc))
			{
				UpdateFeeSubtractionNote();
			}
		};

		// What can be sent is what the ticked coins hold.
		var selection = coins.Selection
			.ToObservableChangeSet()
			.ToCollection()
			.Select(ticked => new Amount(ticked.TotalAmount()))
			.StartWith(Amount.Zero);

		selection.Subscribe(OnSelectionChanged);

		coins.CoinItems
			.ToObservableChangeSet()
			.ToCollection()
			.Select(items => DescribeSelectionHint(items.Count > 0))
			.StartWith(DescribeSelectionHint(coins.CoinItems.Count > 0))
			.BindTo(this, x => x.SelectionHint);

		PasteCommand = ReactiveCommand.CreateFromTask(async () => To = (await ApplicationHelper.GetTextAsync()).WithoutWhitespace());
		QrCommand = ReactiveCommand.CreateFromTask(ScanQrCodeAsync);
		PasteFromClipboardCommand = ReactiveCommand.CreateFromTask<object>(PasteFromClipboardAsync);

		InsertMaxCommand = ReactiveCommand.Create(() =>
		{
			// One payment takes what is left, and only one.
			foreach (var recipient in _additionalRecipients)
			{
				recipient.IsSubtractFee = false;
			}

			IsPrimarySubtractFee = true;
			RecalculateMaxAmount();
		});

		AddRecipientCommand = ReactiveCommand.Create(OnAddRecipient, this.WhenAnyValue(x => x.IsBip21).Select(isBip21 => !isBip21));

		_additionalRecipients.CollectionChanged += (_, _) =>
		{
			IsPayToMany = _additionalRecipients.Count > 0;
			_recipientsChanged.OnNext(Unit.Default);
		};

		var formChanged = this.WhenAnyValue(
				x => x.AmountBtc,
				x => x.To,
				x => x.FeeRateText,
				x => x.IsPrimarySubtractFee,
				x => x.SelectedAmount,
				x => x.IsWalletLoaded)
			.ToSignal()
			.Merge(_recipientsChanged);

		var canSend = formChanged.Select(_ =>
		{
			if (!IsWalletLoaded || !SelectedAmount.HasBalance || string.IsNullOrEmpty(To) || AmountBtc is not > 0 || ParseFeeRate(FeeRateText) is null)
			{
				return false;
			}

			if (_additionalRecipients.Count > 0)
			{
				ValidateAdditionalRecipientBalances();

				if (!_additionalRecipients.All(r => r.IsValid))
				{
					return false;
				}
			}

			return !Validations.AnyErrors;
		});

		// The way on of the payment: it shows the transaction, and nothing is signed before that.
		SendCommand = ReactiveCommand.CreateFromTask(OnSendAsync, canSend.ObserveOn(RxApp.MainThreadScheduler));

		// An address can be generated at any time, also while the wallet still processes blocks.
		ReceiveCommand = ReactiveCommand.Create(() => Navigate().To().Receive(walletModel));
	}

	/// <summary>
	/// The box of the coinjoins, below the tabs. It shows the tab that is selected, or the round that is under way.
	/// </summary>
	public CoinJoinBoxViewModel Box { get; }

	/// <summary>The payments that wait for a WabiSabi round to make them. They are on the main screen of WabiSabi.</summary>
	public CoinJoinPaymentsViewModel Payments { get; }

	/// <summary>The market for anonymity points. Its two sides are the main screens of Taker and Maker.</summary>
	public MarketViewModel Market { get; }

	/// <summary>How wide the dialog may get. The three columns of the market take more than a form does.</summary>
	public override double DialogMaxWidth => IsMarket(GetTab(SelectedTab)) ? MarketDialogMaxWidth : base.DialogMaxWidth;

	/// <summary>How high the dialog may get.</summary>
	public override double DialogMaxHeight => IsMarket(GetTab(SelectedTab)) ? MarketDialogMaxHeight : base.DialogMaxHeight;

	/// <summary>Shows the transaction that the payment describes. It is confirmed and sent from that page.</summary>
	public ICommand SendCommand { get; }

	/// <summary>Opens the addresses that await a payment. A new address is generated from there.</summary>
	public ICommand ReceiveCommand { get; }

	public IndexedCollection<RecipientRowViewModel> AdditionalRecipients { get; }

	public bool IsQrButtonVisible => UiContext.QrCodeReader.IsPlatformSupported;

	public ICommand PasteCommand { get; }

	public ICommand QrCommand { get; }

	public ICommand InsertMaxCommand { get; }

	public ICommand PasteFromClipboardCommand { get; }

	public ICommand AddRecipientCommand { get; }

	private decimal AvailableAmountBtc => SelectedAmount.Btc.ToDecimal(MoneyUnit.BTC);

	/// <summary>
	/// The fee rate that the form offers until the user types one: what the node estimates for a confirmation within two blocks,
	/// which is the fee rate of the title bar. Null while the node gave no such estimate.
	/// </summary>
	public static FeeRate? GetSuggestedFeeRate(FeeRateEstimations? estimations) =>
		estimations?.Estimations.FirstOrDefault(x => x.Key == SuggestedFeeRateTarget).Value;

	/// <summary>
	/// Whether the form says that the fee is taken from the payment. It says so for a payment that takes all that the picked
	/// coins hold, as long as that is something: with the last coin unticked there is no payment to take a fee from.
	/// A message about the amount goes first, as the two would stand in the same place.
	/// </summary>
	public static bool ShowsFeeSubtractionNote(bool takesAll, decimal? amountBtc, bool hasAmountMessage) =>
		takesAll && amountBtc > 0 && !hasAmountMessage;

	/// <summary>The place of a tab in the row of the tabs.</summary>
	public static int GetTabIndex(CoinJoinTab tab)
	{
		for (var index = 0; index < CoinJoinBoxViewModel.AllTabs.Count; index++)
		{
			if (CoinJoinBoxViewModel.AllTabs[index] == tab)
			{
				return index;
			}
		}

		return 0;
	}

	/// <summary>The tab at a place in the row of the tabs. A place that the row does not have is the first tab's.</summary>
	public static CoinJoinTab GetTab(int index) =>
		index >= 0 && index < CoinJoinBoxViewModel.AllTabs.Count
			? CoinJoinBoxViewModel.AllTabs[index]
			: CoinJoinBoxViewModel.DefaultTab;

	/// <summary>Whether a tab shows a side of the market for anonymity points.</summary>
	public static bool IsMarket(CoinJoinTab tab) => tab is CoinJoinTab.Taker or CoinJoinTab.Maker;

	/// <summary>
	/// What the dialog says under its title about the coins that the tab works with: the ones that are ticked on the
	/// Coins tab, except for a maker, whose coins are picked on its own tab. While the two sides of the market show
	/// the examples of their design, they say that in its place.
	/// </summary>
	public static string DescribeCoins(CoinJoinTab tab, bool isSample = false) =>
		IsMarket(tab) && isSample
			? MarketViewModel.SampleNotice
			: tab == CoinJoinTab.Maker
				? "The coins of a bond or of an offer are ticked in the list on this tab."
				: "Everything here works with the coins that are ticked on the Coins tab.";

	/// <summary>What the main screen of a tab says about itself, at its top.</summary>
	public static string DescribeScreen(CoinJoinTab tab) =>
		tab switch
		{
			CoinJoinTab.Payjoin =>
				"A payment spends the ticked coins. Payjoin is not available yet: Send and Receive work the regular way.",
			CoinJoinTab.WabiSabi =>
				"Play starts one round with the ticked coins, ten at most. A round makes the payments that are queued here, four at most. The queue is cleared when you close Black Hole.",
			CoinJoinTab.ZeroLink =>
				"Play starts one round with one ticked coin: a coin of a standard amount that the coordinator has a round of. The round gives one output of that amount less the fee.",
			// The two sides of the market say what they are by what they show.
			_ => ""
		};

	/// <summary>What stands next to the amount while no coin is picked: what is to be done first.</summary>
	public static string DescribeSelectionHint(bool hasCoins) =>
		hasCoins ? "Tick the coins to spend on the Coins tab" : "The wallet has no coins to spend";

	/// <summary>Reads a fee rate in satoshis per virtual byte. Null if it is not one that a transaction can pay.</summary>
	public static FeeRate? ParseFeeRate(string text) => RedeemBondViewModel.ParseFeeRate(text.Trim());

	/// <summary>Writes a fee rate the way the fee rate box takes it.</summary>
	public static string FormatFeeRate(FeeRate feeRate) => feeRate.SatoshiPerByte.ToString("0.##", CultureInfo.InvariantCulture);

	/// <summary>
	/// Tells what is wrong with the amounts of the payments, if anything is. Without picked coins nothing is: the form may be
	/// filled in before the coins are picked, and it cannot be sent without them anyway. An amount of nothing is not
	/// an error either: that is the amount before one was typed, while a number that starts with a zero is being typed,
	/// and what "Max" leaves once the last coin was unticked. The form cannot be sent with it, and that is all.
	/// </summary>
	/// <param name="amountBtc">The amount of the first payment.</param>
	/// <param name="totalBtc">The amounts of all the payments together.</param>
	/// <param name="selectedBtc">What the picked coins hold.</param>
	public static string? DescribeAmountError(decimal amountBtc, decimal totalBtc, decimal selectedBtc)
	{
		if (amountBtc > Constants.MaximumNumberOfBitcoins)
		{
			return "Amount must be less than the total supply of BTC.";
		}

		if (amountBtc <= 0)
		{
			return null;
		}

		if (selectedBtc > 0 && totalBtc > selectedBtc)
		{
			return "The selected coins hold less than that.";
		}

		return null;
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// The dialog shows again: the transaction that the following pages showed was not sent from there, and the
		// coinjoins need not wait for it any longer.
		LeaveSendWorkflow();

		// The estimates are asked from the application: a wallet that was just created has none of its own until the node changes them.
		UiContext.Services.EventBus.AsObservable<MiningFeeRatesChanged>()
			.Select(e => e.AllFeeEstimate)
			.StartWith(UiContext.Services.GetFeeRateEstimations())
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(SuggestFeeRate)
			.DisposeWith(disposables);

		// A round changes the payments that wait for it without telling: they are looked up for as long as the dialog shows.
		Payments.Activate(disposables);

		// The sides of the market follow the wallet and the relays while one of them shows, and not otherwise.
		this.WhenAnyValue(x => x.IsMarketTab)
			.Subscribe(isMarketTab =>
			{
				if (isMarketTab)
				{
					Market.Activate();
				}
				else
				{
					Market.Deactivate();
				}
			})
			.DisposeWith(disposables);

		disposables.Add(Disposable.Create(Market.Deactivate));
	}

	protected override void OnNavigatedFrom(bool isInHistory)
	{
		base.OnNavigatedFrom(isInHistory);

		// The dialog is closed, with whatever page it showed last: the transaction was sent or given up.
		if (!isInHistory)
		{
			LeaveSendWorkflow();
		}
	}

	/// <summary>
	/// Lets the coinjoins of the wallet go on after the pages of a transaction: a round that those pages interrupted
	/// starts again.
	/// </summary>
	private void LeaveSendWorkflow()
	{
		if (!_isInSendWorkflow)
		{
			return;
		}

		_isInSendWorkflow = false;
		_coinJoinManager?.WalletLeftSendWorkflow(_wallet);
	}

	/// <summary>Fills the fee rate box with the estimate of the node, unless the user typed a fee rate of their own.</summary>
	private void SuggestFeeRate(FeeRateEstimations? estimations)
	{
		if (GetSuggestedFeeRate(estimations) is not { } feeRate)
		{
			return;
		}

		var isUntouched = FeeRateText == _suggestedFeeRateText;
		_suggestedFeeRateText = FormatFeeRate(feeRate);

		if (isUntouched)
		{
			FeeRateText = _suggestedFeeRateText;
		}
	}

	private void UpdateFeeSubtractionNote()
	{
		var hasAmountMessage = ((INotifyDataErrorInfo)this).GetErrors(nameof(AmountBtc)).Cast<object>().Any();

		IsFeeSubtractionNoteVisible = ShowsFeeSubtractionNote(IsPrimarySubtractFee, AmountBtc, hasAmountMessage);
	}

	private void OnSelectionChanged(Amount selectedAmount)
	{
		SelectedAmount = selectedAmount;
		HasSelection = selectedAmount.HasBalance;

		// A payment that takes all follows the coins. The other amounts are checked against them again.
		RecalculateMaxAmount();
		this.RaisePropertyChanged(nameof(AmountBtc));
		_recipientsChanged.OnNext(Unit.Default);
	}

	private void RecalculateMaxAmount()
	{
		_isRecalculating = true;

		try
		{
			var maxAdditionalRecipient = _additionalRecipients.FirstOrDefault(r => r.IsSubtractFee);

			if (IsPrimarySubtractFee)
			{
				// The first payment gets what the others leave.
				var otherAmounts = _additionalRecipients
					.Where(r => r.AmountBtc.HasValue)
					.Sum(r => r.AmountBtc!.Value);
				var newAmount = Math.Max(0m, AvailableAmountBtc - otherAmounts);

				if (AmountBtc != newAmount)
				{
					AmountBtc = newAmount;
				}
			}
			else if (maxAdditionalRecipient is not null)
			{
				var remaining = GetRemainingBalanceFor(maxAdditionalRecipient);

				if (maxAdditionalRecipient.AmountBtc != remaining)
				{
					maxAdditionalRecipient.AmountBtc = remaining;
				}
			}
		}
		finally
		{
			_isRecalculating = false;
		}
	}

	private decimal GetRemainingBalanceFor(RecipientRowViewModel excludeRow)
	{
		var primaryAmount = AmountBtc ?? 0m;
		var otherAdditionalAmounts = _additionalRecipients
			.Where(r => r != excludeRow && r.AmountBtc.HasValue)
			.Sum(r => r.AmountBtc!.Value);

		return Math.Max(0m, AvailableAmountBtc - primaryAmount - otherAdditionalAmounts);
	}

	private decimal GetTotalAmountBtc() =>
		(AmountBtc ?? 0m) + _additionalRecipients.Where(r => r.AmountBtc.HasValue).Sum(r => r.AmountBtc!.Value);

	private void ValidateAdditionalRecipientBalances()
	{
		var totalAmountBtc = GetTotalAmountBtc();
		var anySubtractFee = IsPrimarySubtractFee || _additionalRecipients.Any(r => r.IsSubtractFee);
		var overBudget = totalAmountBtc > AvailableAmountBtc;
		var needsSubtractFee = totalAmountBtc == AvailableAmountBtc && !anySubtractFee;

		foreach (var row in _additionalRecipients)
		{
			if (overBudget && row.AmountBtc > 0 && !row.IsSubtractFee)
			{
				row.AmountError = "The selected coins hold less than the payments together.";
			}
			else if (needsSubtractFee && row.AmountBtc > 0)
			{
				row.AmountError = "The payments take all of the selected coins. Use Max on one of them to cover the transaction fee.";
			}
			else
			{
				row.AmountError = null;
			}
		}
	}

	private void OnAddRecipient()
	{
		var row = new RecipientRowViewModel(
			UiContext,
			_walletModel.Network,
			onRemove: r =>
			{
				_additionalRecipients.Remove(r);
				r.Dispose();
			},
			onInsertMax: r =>
			{
				// One payment takes what is left, and only one.
				IsPrimarySubtractFee = false;

				foreach (var other in _additionalRecipients.Where(x => x != r))
				{
					other.IsSubtractFee = false;
				}

				r.IsSubtractFee = true;
				RecalculateMaxAmount();
			},
			scanQrCodeAsync: ShowQrCodeCameraAsync,
			isQrButtonVisible: IsQrButtonVisible,
			isRecalculating: () => _isRecalculating);

		row.WhenAnyValue(r => r.AmountBtc, r => r.To, r => r.IsSubtractFee)
			.Subscribe(_ => _recipientsChanged.OnNext(Unit.Default));

		_additionalRecipients.Add(row);
	}

	private static Destination AddressToDestination(Address parsedAddress)
	{
		return parsedAddress switch
		{
			Address.Bitcoin bitcoin => new Destination.Loudly(bitcoin.Address.ScriptPubKey),
			Address.Bip21Uri { Address: Address.Bitcoin bitcoin } => new Destination.Loudly(bitcoin.Address.ScriptPubKey),
			Address.Bip21Uri { Address: Address.SilentPayment silentPayment } => new Destination.Silent(silentPayment.Address),
			Address.SilentPayment silentPayment => new Destination.Silent(silentPayment.Address),
			_ => throw new ArgumentException("Unknown address type")
		};
	}

	private async Task OnSendAsync()
	{
		if (AmountBtc is not { } amountBtc || _parsedAddress is not { } parsedAddress || ParseFeeRate(FeeRateText) is not { } feeRate)
		{
			return;
		}

		var coins = _coins.Selection.GetSmartCoins().ToList();

		if (coins.Count == 0)
		{
			return;
		}

		var amount = new Money(amountBtc, MoneyUnit.BTC);

		var additionalRecipients = _additionalRecipients
			.Where(r => r.IsValid && r.ParsedAddress is not null)
			.Select(r => new RecipientInfo(
				AddressToDestination(r.ParsedAddress!),
				new Money(r.AmountBtc!.Value, MoneyUnit.BTC),
				IsSubtractFee: r.IsSubtractFee))
			.ToList();

		// A single payment of all that the coins hold pays its fee from itself, unless the payer was asked for exactly that amount.
		var primarySubtractFee = additionalRecipients.Count > 0
			? IsPrimarySubtractFee
			: amount == coins.Sum(x => x.Amount) && !IsFixedAmount;

		var transactionInfo = new TransactionInfo(AddressToDestination(parsedAddress))
		{
			Amount = amount,
			IsFixedAmount = IsFixedAmount,
			SubtractFee = primarySubtractFee,
			AdditionalRecipients = additionalRecipients,
			FeeRate = feeRate,

			// A fee rate that the user typed is changed by typing on the next page too. The estimate of the node is changed by choosing another one.
			IsCustomFeeUsed = FeeRateText != _suggestedFeeRateText
		};

		var sendFlow = new SendFlowModel(_wallet, _walletModel, coins, UiContext.Services)
		{
			TransactionInfo = transactionInfo,
			Sent = Reset
		};

		// No coinjoin is to start with the coins while the transaction is looked at, and one that runs gives them back.
		// The coinjoins go on when this dialog shows again or is closed.
		if (_coinJoinManager is { } coinJoinManager)
		{
			coinJoinManager.WalletEnteredSendWorkflow(_walletModel.Id);
			_isInSendWorkflow = true;

			await coinJoinManager.WalletEnteredSendingAsync(_wallet);
		}

		Navigate().To().TransactionPreview(_walletModel, sendFlow);
	}

	/// <summary>Empties the form after its payment was sent. The fee rate goes back to the estimate of the node.</summary>
	private void Reset()
	{
		foreach (var recipient in _additionalRecipients)
		{
			recipient.Dispose();
		}

		_additionalRecipients.Clear();
		_coins.SelectAll(false);

		_parsedAddress = null;
		To = "";
		AmountBtc = null;
		IsPrimarySubtractFee = false;
		IsFixedAmount = false;
		IsBip21 = false;
		FeeRateText = _suggestedFeeRateText;

		ClearValidations();
	}

	private async Task PasteFromClipboardAsync(object? parameter)
	{
		if (parameter is not AmountEntryBox)
		{
			return;
		}

		string content = await ApplicationHelper.GetTextAsync();

		if (SelectedAmount.HasBalance && ClipboardObserver.ParseToBtc(content, SelectedAmount.Btc) is { } btc)
		{
			BitcoinContent = btc;
		}
	}

	private async Task<string?> ShowQrCodeCameraAsync() =>
		await UiContext.Navigate().To().ShowQrCameraDialog(_walletModel.Network).GetResultAsync();

	private async Task ScanQrCodeAsync()
	{
		var textContent = await ShowQrCodeCameraAsync();

		if (!string.IsNullOrWhiteSpace(textContent))
		{
			To = textContent;
		}
	}

	private void ValidateAmount(IValidationErrors errors)
	{
		if (AmountBtc is not { } amountBtc)
		{
			return;
		}

		if (DescribeAmountError(amountBtc, GetTotalAmountBtc(), AvailableAmountBtc) is { } error)
		{
			errors.Add(ErrorSeverity.Error, error);
		}

		// An amount of nothing is not an amount yet, so there is nothing to warn about either.
		if (_parsedAddress is Address.SilentPayment && amountBtc is > 0 and < 0.00001m)
		{
			errors.Add(ErrorSeverity.Warning, "Most wallets don't recognize Silent Payments lower than 1000 sats.");
		}
	}

	private void ValidateToField(IValidationErrors errors)
	{
		if (string.IsNullOrEmpty(To))
		{
			return;
		}

		var parseResult = AddressParser.Parse(To, _walletModel.Network);

		if (!parseResult.IsOk)
		{
			errors.Add(ErrorSeverity.Error, parseResult.Error);
		}
	}

	private void ValidateFeeRate(IValidationErrors errors)
	{
		if (string.IsNullOrWhiteSpace(FeeRateText))
		{
			return;
		}

		if (ParseFeeRate(FeeRateText) is null)
		{
			errors.Add(
				ErrorSeverity.Error,
				$"Between {FormatFeeRate(Constants.MinRelayFeeRate)} and {FormatFeeRate(Constants.AbsurdlyHighFeeRate)} sat/vB.");
		}
	}

	private void HandleAddressChange(string? text)
	{
		text = text?.Trim();

		// The text is the one that this method wrote for the address that it read.
		if (text == _parsedAddress?.ToCanonicalAddress(_walletModel.Network))
		{
			return;
		}

		IsFixedAmount = false;
		IsBip21 = false;

		if (string.IsNullOrEmpty(text))
		{
			_parsedAddress = null;
			return;
		}

		var parseResult = AddressParser.Parse(text, _walletModel.Network);

		if (!parseResult.IsOk)
		{
			_parsedAddress = null;
			return;
		}

		_parsedAddress = parseResult.Value;

		switch (parseResult.Value)
		{
			case Address.Bip21Uri bip21:
				IsBip21 = true;
				To = bip21.Address.ToWif(_walletModel.Network);

				if (bip21.Amount is not null)
				{
					AmountBtc = bip21.Amount;
					IsFixedAmount = true;
				}

				break;

			case Address.Bitcoin bitcoin:
				To = bitcoin.Address.ToString();
				break;

			case Address.SilentPayment silentPayment:
				To = silentPayment.Address.ToWip(_walletModel.Network);
				break;
		}
	}
}
