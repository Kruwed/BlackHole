using System.Collections.Generic;
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
using WalletWasabi.Fluent.ViewModels.Wallets.Bonds;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Logging;
using WalletWasabi.Services;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;
using Address = WalletWasabi.Userfacing.Address;
using Constants = WalletWasabi.Helpers.Constants;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// The tab of the coins. It lists the coins of the wallet, and what is done with coins starts here, with the ones that
/// the user ticked: a payment spends them, a WabiSabi coinjoin round gets them, a fidelity bond locks them and an offer
/// offers them to the takers of coinjoins. Receiving starts here too.
/// </summary>
/// <remarks>
/// The ticks belong to the list and to nothing else. They are not kept anywhere, and every one of the four takes the
/// coins that are ticked at the moment it is asked for. For a payment the tab takes the address, the amount and the fee
/// rate above the list. Nothing is signed here: "Continue" shows the transaction, and it is sent from there.
/// </remarks>
[AppLifetime]
public partial class CoinsTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "Coins";

	/// <summary>The fee rate that the form starts with is the estimate for a confirmation within this many blocks, as in the title bar.</summary>
	private const int SuggestedFeeRateTarget = 2;

	private readonly Wallet _wallet;
	private readonly IWalletModel _walletModel;
	private readonly CoinJoinManager? _coinJoinManager;
	private readonly Subject<Unit> _recipientsChanged = new();
	private readonly ObservableCollection<RecipientRowViewModel> _additionalRecipients = [];

	private Address? _parsedAddress;
	private bool _isRecalculating;

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
	[AutoNotify] private bool _hasSelection;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private Amount _selectedAmount = Amount.Zero;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletLoaded;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _selectionHint = DescribeSelectionHint(hasCoins: false);
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isFeeSubtractionNoteVisible;

	public CoinsTabViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		_wallet = wallet;
		_walletModel = walletModel;
		_coinJoinManager = UiContext.Services.GetHostedService<CoinJoinManager>();

		CoinList = new CoinListViewModel(uiContext, walletModel.Coins, [], allowCoinjoiningCoinSelection: true, allowSelection: true);
		AdditionalRecipients = new IndexedCollection<RecipientRowViewModel>(_additionalRecipients);

		// What the wallet knows about its coins is not complete until it has processed the blocks of the node, so
		// everything that takes coins waits for that.
		walletModel.WhenAnyValue(x => x.IsLoaded)
			.BindTo(this, x => x.IsWalletLoaded);

		// Every kind of coinjoin has its box with its controls. A round of either gets the coins that are ticked in the list.
		WabiSabi = new CoinJoinBoxViewModel(uiContext, walletModel, wallet, CoinJoinProtocol.WabiSabi, StartRoundAsync);
		ZeroLink = new CoinJoinBoxViewModel(uiContext, walletModel, wallet, CoinJoinProtocol.ZeroLink, StartRoundAsync);

		// An address can be generated at any time. A bond and an offer are about the coins of the wallet.
		var isWalletLoaded = this.WhenAnyValue(x => x.IsWalletLoaded);

		ReceiveCommand = ReactiveCommand.Create(() => UiContext.Navigate().To().Receive(walletModel));
		BondCommand = ReactiveCommand.Create(() => UiContext.Navigate().To().Bonds(walletModel, wallet, CoinList), isWalletLoaded);
		OfferCommand = ReactiveCommand.Create(() => UiContext.Navigate().To().MakerOffer(walletModel, CoinList), isWalletLoaded);

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

		// What can be sent is what the picked coins hold.
		var selection = CoinList.Selection
			.ToObservableChangeSet()
			.ToCollection()
			.Select(coins => new Amount(coins.TotalAmount()))
			.StartWith(Amount.Zero);

		selection.Subscribe(OnSelectionChanged);

		CoinList.CoinItems
			.ToObservableChangeSet()
			.ToCollection()
			.Select(coins => DescribeSelectionHint(coins.Count > 0))
			.StartWith(DescribeSelectionHint(CoinList.CoinItems.Count > 0))
			.BindTo(this, x => x.SelectionHint);

		PasteCommand = ReactiveCommand.CreateFromTask(async () => To = (await ApplicationHelper.GetTextAsync()).WithoutWhitespace());
		QrCommand = ReactiveCommand.CreateFromTask(ScanQrCodeAsync);
		PasteFromClipboardCommand = ReactiveCommand.CreateFromTask<object>(PasteFromClipboardAsync);
		ToggleSelectionCommand = ReactiveCommand.Create(() => CoinList.SelectAll(!CoinList.Selection.Any()));

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

		var canContinue = formChanged.Select(_ =>
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

		ContinueCommand = ReactiveCommand.CreateFromTask(OnContinueAsync, canContinue.ObserveOn(RxApp.MainThreadScheduler));
	}

	/// <summary>
	/// The coins of the wallet. The ones that are ticked are the ones that a payment may spend, that a coinjoin round
	/// gets, that a bond locks and that an offer offers. No coin is ticked before the user ticks it: a coin that
	/// arrives, also out of a coinjoin, is not.
	/// </summary>
	public CoinListViewModel CoinList { get; }

	/// <summary>The WabiSabi coinjoins: a round gets the ticked coins that it takes, ten at most.</summary>
	public CoinJoinBoxViewModel WabiSabi { get; }

	/// <summary>
	/// The ZeroLink coinjoins: a round gets one ticked coin, of a standard denomination that the coordinator has a
	/// round of, and gives one output of that denomination less the fee.
	/// </summary>
	public CoinJoinBoxViewModel ZeroLink { get; }

	/// <summary>Opens the addresses that await a payment. A new address is generated from there.</summary>
	public ICommand ReceiveCommand { get; }

	/// <summary>Opens the fidelity bonds of the wallet. A new bond locks the coins that are ticked.</summary>
	public ICommand BondCommand { get; }

	/// <summary>Opens the offer of the wallet as a market maker. An offer offers the coins that are ticked.</summary>
	public ICommand OfferCommand { get; }

	public IndexedCollection<RecipientRowViewModel> AdditionalRecipients { get; }

	public bool IsQrButtonVisible => UiContext.QrCodeReader.IsPlatformSupported;

	public ICommand PasteCommand { get; }

	public ICommand QrCommand { get; }

	public ICommand InsertMaxCommand { get; }

	public ICommand PasteFromClipboardCommand { get; }

	public ICommand AddRecipientCommand { get; }

	/// <summary>Ticks every coin, or none if some are ticked.</summary>
	public ICommand ToggleSelectionCommand { get; }

	/// <summary>Shows the transaction that the form describes. It is confirmed and sent from that page.</summary>
	public ICommand ContinueCommand { get; }

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

	/// <summary>
	/// Tells the wallet's choice of coins for the round that Play starts: every coin of the list is named, the ticked
	/// ones as chosen and the others as not chosen. So the round gets what is ticked now and nothing that an earlier
	/// round was started with.
	/// </summary>
	public static IReadOnlyDictionary<OutPoint, bool> GetRoundSelection(IEnumerable<(OutPoint Outpoint, bool IsTicked)> coins)
	{
		var selection = new Dictionary<OutPoint, bool>();

		foreach (var (outpoint, isTicked) in coins)
		{
			selection[outpoint] = isTicked;
		}

		return selection;
	}

	/// <summary>What stands next to the amount while no coin is picked: what is to be done first.</summary>
	public static string DescribeSelectionHint(bool hasCoins) =>
		hasCoins ? "Select the coins to spend below" : "The wallet has no coins to spend";

	/// <summary>Reads a fee rate in satoshis per virtual byte. Null if it is not one that a transaction can pay.</summary>
	public static FeeRate? ParseFeeRate(string text) => RedeemBondViewModel.ParseFeeRate(text.Trim());

	/// <summary>Writes a fee rate the way the fee rate box takes it.</summary>
	public static string FormatFeeRate(FeeRate feeRate) => feeRate.SatoshiPerByte.ToString("0.##", CultureInfo.InvariantCulture);

	/// <summary>
	/// Tells what is wrong with the amounts of the payments, if anything is. Without picked coins nothing is: the form may be
	/// filled in before the coins are picked, and it cannot be continued without them anyway. An amount of nothing is not
	/// an error either: that is the amount before one was typed, while a number that starts with a zero is being typed,
	/// and what "Max" leaves once the last coin was unticked. The form cannot be continued with it, and that is all.
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

	protected override void OnActivated(CompositeDisposable disposables)
	{
		base.OnActivated(disposables);

		// The estimates are asked from the application: a wallet that was just created has none of its own until the node changes them.
		UiContext.Services.EventBus.AsObservable<MiningFeeRatesChanged>()
			.Select(e => e.AllFeeEstimate)
			.StartWith(UiContext.Services.GetFeeRateEstimations())
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(SuggestFeeRate)
			.DisposeWith(disposables);
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

	/// <summary>
	/// Starts one round of the given kind with the coins that are ticked. The wallet is told first which coins they
	/// are: a round takes of the coins that the wallet has as chosen for coinjoins, ten at most in a WabiSabi round and
	/// one in a ZeroLink round. If the wallet could not be told, no round is started, as it would get other coins than
	/// the ticked ones.
	/// </summary>
	private async Task StartRoundAsync(CoinJoinProtocol protocol)
	{
		if (_walletModel.Coinjoin is not { } coinjoin)
		{
			return;
		}

		try
		{
			var ticks = CoinList.CoinItems.Select(row => (row.Coin.GetSmartCoin().Outpoint, row.IsSelected)).ToArray();

			await _walletModel.Coins.SelectForCoinjoinAsync(GetRoundSelection(ticks));
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			return;
		}

		await coinjoin.StartAsync(protocol);
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

	private async Task OnContinueAsync()
	{
		if (AmountBtc is not { } amountBtc || _parsedAddress is not { } parsedAddress || ParseFeeRate(FeeRateText) is not { } feeRate)
		{
			return;
		}

		var coins = CoinList.Selection.GetSmartCoins().ToList();

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
		if (_coinJoinManager is { } coinJoinManager)
		{
			coinJoinManager.WalletEnteredSendWorkflow(_walletModel.Id);
			await coinJoinManager.WalletEnteredSendingAsync(_wallet);
		}

		UiContext.Navigate().To().TransactionPreview(_walletModel, sendFlow);

		LeaveSendWorkflowWhenTheDialogCloses();
	}

	/// <summary>
	/// The pages that follow are those of a dialog. Whether the transaction was sent from there or the user turned back,
	/// the coinjoins may go on once the dialog is closed.
	/// </summary>
	private void LeaveSendWorkflowWhenTheDialogCloses()
	{
		if (_coinJoinManager is not { } coinJoinManager)
		{
			return;
		}

		if (UiContext.MainViewModel is not { } mainViewModel)
		{
			coinJoinManager.WalletLeftSendWorkflow(_wallet);
			return;
		}

		mainViewModel.DialogScreen.WhenAnyValue(x => x.IsDialogOpen)
			.Where(isOpen => !isOpen)
			.Take(1)
			.Subscribe(_ => coinJoinManager.WalletLeftSendWorkflow(_wallet));
	}

	/// <summary>Empties the form after its payment was sent. The fee rate goes back to the estimate of the node.</summary>
	private void Reset()
	{
		foreach (var recipient in _additionalRecipients)
		{
			recipient.Dispose();
		}

		_additionalRecipients.Clear();
		CoinList.SelectAll(false);

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
