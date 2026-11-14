using NBitcoin;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Extensions;
using WalletWasabi.Fluent.Controls;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Services;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;
using Address = WalletWasabi.Userfacing.Address;
using Constants = WalletWasabi.Helpers.Constants;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

[NavigationMetaData(
	Title = "Send",
	Caption = "Display wallet send dialog",
	IconName = "wallet_action_send",
	Order = 5,
	Category = "Wallet",
	Keywords = new[] { "Wallet", "Send", "Action", },
	NavBarPosition = NavBarPosition.None,
	NavigationTarget = NavigationTarget.DialogScreen,
	Searchable = false)]
public partial class SendViewModel : RoutableViewModel
{
	private readonly Wallet _wallet;
	private readonly IWalletModel _walletModel;
	private readonly SendFlowModel _parameters;
	private readonly ShowQrCodeCameraDialog _showQrCodeCameraDialog;
	private readonly CoinJoinManager? _coinJoinManager;
	private readonly ObservableAsPropertyHelper<Amount?> _balanceLatest;

	private Address? _parsedAddress;

	[AutoNotify] private string _caption = "";
	[AutoNotify] private string _to;
	[AutoNotify] private decimal? _amountBtc;
	[AutoNotify] private bool _isFixedAmount;
	[AutoNotify] private bool _displaySilentPaymentInfo;
	[AutoNotify] private bool _isFixedAddress;
	[AutoNotify] private string? _bitcoinContent;
	[AutoNotify] private bool _isPayToMany;
	[AutoNotify] private bool _isPrimarySubtractFee;
	[AutoNotify] private bool _isBip21;

	private readonly Subject<Unit> _recipientsChanged = new();
	private readonly ObservableCollection<RecipientRowViewModel> _additionalRecipients;
	private bool _isRecalculating;

	public SendViewModel(UiContext uiContext, IWalletModel walletModel, SendFlowModel parameters, ShowQrCodeCameraDialog showQrCodeCameraDialog) : base(uiContext)
	{
		_to = "";

		_wallet = parameters.Wallet;
		_walletModel = walletModel;
		_parameters = parameters;
		_showQrCodeCameraDialog = showQrCodeCameraDialog;
		_coinJoinManager = UiContext.Services.GetHostedService<CoinJoinManager>();

		// What can be sent is what the coins that the user picked hold.
		Balance = Observable.Return(new Amount(_parameters.AvailableAmount));

		_additionalRecipients = new ObservableCollection<RecipientRowViewModel>();
		AdditionalRecipients = new IndexedCollection<RecipientRowViewModel>(_additionalRecipients);

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);

		EnableBack = true;

		this.ValidateProperty(x => x.To, ValidateToField);
		this.ValidateProperty(x => x.AmountBtc, ValidateAmount);

		this.WhenAnyValue(x => x.To)
			.Skip(1)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(HandleAddressChange);

		this.WhenAnyValue(x => x.Balance)
			.Switch()
			.ToProperty(this, vm => vm.BalanceLatest, out _balanceLatest);

		// Clear primary subtract fee when user manually changes amount
		this.WhenAnyValue(x => x.AmountBtc)
			.Skip(1)
			.Where(_ => IsPrimarySubtractFee && !_isRecalculating)
			.Subscribe(_ => IsPrimarySubtractFee = false);

		PasteCommand = ReactiveCommand.CreateFromTask(async () => await OnPasteAsync());
		AutoPasteCommand = ReactiveCommand.CreateFromTask(OnAutoPasteAsync);
		InsertMaxCommand = ReactiveCommand.Create(() =>
		{
			// Clear all other subtract fee flags and set primary
			foreach (var recipient in _additionalRecipients)
			{
				recipient.IsSubtractFee = false;
			}
			IsPrimarySubtractFee = true;
			RecalculateMaxAmount();
		});
		QrCommand = ReactiveCommand.Create(ShowQrCameraDialogAsync);

		var canAddRecipient = this.WhenAnyValue(x => x.IsBip21)
			.Select(isBip21 => !isBip21);
		AddRecipientCommand = ReactiveCommand.Create(OnAddRecipient, canAddRecipient);

		_additionalRecipients.CollectionChanged += (_, _) =>
		{
			IsPayToMany = _additionalRecipients.Count > 0;
			_recipientsChanged.OnNext(Unit.Default);
		};

		var primaryChanged = this.WhenAnyValue(
				x => x.AmountBtc,
				x => x.To,
				x => x.IsPrimarySubtractFee)
			.Select(_ => Unit.Default);

		var nextCommandCanExecute = primaryChanged
			.Merge(_recipientsChanged)
			.Select(_ =>
			{
				var allFilled = !string.IsNullOrEmpty(To) && AmountBtc > 0;
				var hasError = Validations.AnyErrors;

				if (allFilled && _additionalRecipients.Count > 0)
				{
					ValidateAdditionalRecipientBalances();
					allFilled = _additionalRecipients.All(r => r.IsValid);
				}

				return allFilled && !hasError;
			});

		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync, nextCommandCanExecute);
		PasteFromClipboardCommand = ReactiveCommand.CreateFromTask<object>(PasteFromClipboardAsync);
	}

	public IObservable<Amount> Balance { get; }

	public Amount? BalanceLatest => _balanceLatest.Value;

	public bool IsQrButtonVisible => UiContext.QrCodeReader.IsPlatformSupported;

	public bool IsNotInDonationWorkflow => !_parameters.Donate;

	public ICommand PasteCommand { get; }

	public ICommand AutoPasteCommand { get; }

	public ICommand QrCommand { get; }

	public ICommand InsertMaxCommand { get; }

	public ICommand? PasteFromClipboardCommand { get; }

	public IndexedCollection<RecipientRowViewModel> AdditionalRecipients { get; }

	public ICommand AddRecipientCommand { get; }

	private void RecalculateMaxAmount()
	{
		_isRecalculating = true;
		try
		{
			// Find which recipient (if any) has subtract fee enabled
			var maxAdditionalRecipient = _additionalRecipients.FirstOrDefault(r => r.IsSubtractFee);

			if (IsPrimarySubtractFee)
			{
				// Primary recipient gets the remainder
				var otherAmounts = _additionalRecipients
					.Where(r => r.AmountBtc.HasValue)
					.Sum(r => r.AmountBtc!.Value);
				var newAmount = Math.Max(0m, _parameters.AvailableAmountBtc - otherAmounts);

				// Only update if different to avoid unnecessary updates
				if (AmountBtc != newAmount)
				{
					AmountBtc = newAmount;
				}
			}
			else if (maxAdditionalRecipient is not null)
			{
				// Additional recipient gets the remainder
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
		var remaining = _parameters.AvailableAmountBtc - primaryAmount - otherAdditionalAmounts;
		return Math.Max(0m, remaining);
	}

	private void ValidateAdditionalRecipientBalances()
	{
		var totalAmountBtc = (AmountBtc ?? 0m) + _additionalRecipients
			.Where(r => r.AmountBtc.HasValue)
			.Sum(r => r.AmountBtc!.Value);

		var anySubtractFee = IsPrimarySubtractFee || _additionalRecipients.Any(r => r.IsSubtractFee);
		var overBudget = totalAmountBtc > _parameters.AvailableAmountBtc;
		var needsSubtractFee = totalAmountBtc == _parameters.AvailableAmountBtc && !anySubtractFee;

		foreach (var row in _additionalRecipients)
		{
			if (overBudget && row.AmountBtc > 0 && !row.IsSubtractFee)
			{
				row.AmountError = "Insufficient funds to cover the total amount requested.";
			}
			else if (needsSubtractFee && row.AmountBtc > 0)
			{
				row.AmountError = "Total equals available balance. Use Max on one recipient to cover the transaction fee.";
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
				// Clear all other subtract fee flags and set this one
				IsPrimarySubtractFee = false;
				foreach (var other in _additionalRecipients.Where(x => x != r))
				{
					other.IsSubtractFee = false;
				}
				r.IsSubtractFee = true;
				RecalculateMaxAmount();
			},
			scanQrCodeAsync: async () => await _showQrCodeCameraDialog(this, _walletModel.Network),
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

	private async Task OnNextAsync()
	{
		if (AmountBtc is not { } amountBtc)
		{
			return;
		}

		if (_parsedAddress is not { } parsedAddress)
		{
			return;
		}

		var amount = new Money(amountBtc, MoneyUnit.BTC);
		Destination destination = AddressToDestination(parsedAddress);

		var additionalRecipients = _additionalRecipients
			.Where(r => r.IsValid && r.ParsedAddress is not null)
			.Select(r => new RecipientInfo(
				AddressToDestination(r.ParsedAddress!),
				new Money(r.AmountBtc!.Value, MoneyUnit.BTC),
				IsSubtractFee: r.IsSubtractFee))
			.ToList();

		var isPayToMany = additionalRecipients.Count > 0;

		var primarySubtractFee = isPayToMany
			? IsPrimarySubtractFee
			: amount == _parameters.AvailableCoins.TotalAmount() && !IsFixedAmount;

		var transactionInfo = new TransactionInfo(destination, _walletModel.Settings.AnonScoreTarget)
		{
			Amount = amount,
			IsFixedAmount = IsFixedAmount,
			SubtractFee = primarySubtractFee,
			AdditionalRecipients = additionalRecipients
		};

		if (_coinJoinManager is { } coinJoinManager)
		{
			await coinJoinManager.WalletEnteredSendingAsync(_wallet);
		}

		var sendParameters = _parameters with { TransactionInfo = transactionInfo };

		Navigate().To().TransactionPreview(_walletModel, sendParameters);
	}

	private async Task PasteFromClipboardAsync(object? parameter)
	{
		if (parameter is not AmountEntryBox)
		{
			return;
		}

		string content = await ApplicationHelper.GetTextAsync();

		var latestBalance = BalanceLatest;
		if (latestBalance is not null)
		{
			var btc = ClipboardObserver.ParseToBtc(content, latestBalance.Btc);
			if (btc is not null)
			{
				BitcoinContent = btc;
			}
		}
	}

	private async Task OnAutoPasteAsync()
	{
		var isAutoPasteEnabled = UiContext.ApplicationSettings.AutoPaste;

		if (string.IsNullOrEmpty(To) && isAutoPasteEnabled && IsNotInDonationWorkflow)
		{
			await PasteIfValidAddressAsync();
		}
	}

	private async Task<string> GetClipboardTextAsync()
	{
		var text = await ApplicationHelper.GetTextAsync();
		return text.WithoutWhitespace();
	}

	private async Task OnPasteAsync()
	{
		To = await GetClipboardTextAsync();
	}

	private async Task PasteIfValidAddressAsync()
	{
		var text = await GetClipboardTextAsync();

		if (AddressParser.Parse(text, _walletModel.Network).IsOk)
		{
			To = text;
		}
	}

	private async Task ShowQrCameraDialogAsync()
	{
		var textContent = await _showQrCodeCameraDialog(this, _walletModel.Network);

		if (!string.IsNullOrWhiteSpace(textContent))
		{
			To = textContent;
		}
	}

	private void ValidateAmount(IValidationErrors errors)
	{
		if (AmountBtc is null)
		{
			return;
		}

		if (AmountBtc > Constants.MaximumNumberOfBitcoins)
		{
			errors.Add(ErrorSeverity.Error, "Amount must be less than the total supply of BTC.");
		}
		else if (AmountBtc <= 0)
		{
			errors.Add(ErrorSeverity.Error, "Amount must be more than 0 BTC");
		}
		else
		{
			var totalAmountBtc = AmountBtc.Value + _additionalRecipients.Where(r => r.AmountBtc.HasValue).Sum(r => r.AmountBtc!.Value);
			if (totalAmountBtc > _parameters.AvailableAmountBtc)
			{
				errors.Add(ErrorSeverity.Error, "Insufficient funds to cover the total amount requested.");
			}
		}

		if (_parsedAddress is Address.SilentPayment && AmountBtc < 0.00001m)
		{
			errors.Add(ErrorSeverity.Warning, "Most wallets don't recognize Silent Payments lower than 1000 sats.");
		}
	}

	private void ValidateToField(IValidationErrors errors)
	{
		var parseResult = AddressParser.Parse(To, _walletModel.Network);
		if (!parseResult.IsOk)
		{
			errors.Add(ErrorSeverity.Error, parseResult.Error);
			return;
		}
	}

	private void HandleAddressChange(string? text)
	{
		text = text?.Trim();

		// Skip if this matches the canonical form of the current parsed address
		if (text == _parsedAddress?.ToCanonicalAddress(_walletModel.Network))
		{
			return;
		}

		if (string.IsNullOrEmpty(text))
		{
			_parsedAddress = null;
			IsFixedAmount = false;
			IsBip21 = false;
			DisplaySilentPaymentInfo = false;
			return;
		}

		// Reset state for new input
		IsFixedAmount = false;
		IsBip21 = false;

		var parseResult = AddressParser.Parse(text, _walletModel.Network);
		if (!parseResult.IsOk)
		{
			_parsedAddress = null;
			DisplaySilentPaymentInfo = false;
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

				DisplaySilentPaymentInfo = false;
				break;

			case Address.Bitcoin bitcoin:
				To = bitcoin.Address.ToString();
				DisplaySilentPaymentInfo = false;
				break;

			case Address.SilentPayment silentPayment:
				To = silentPayment.Address.ToWip(_walletModel.Network);
				DisplaySilentPaymentInfo = _parameters.Donate;
				break;

			default:
				DisplaySilentPaymentInfo = false;
				break;
		}
	}

	protected override void OnNavigatedTo(bool inHistory, CompositeDisposable disposables)
	{
		if (!inHistory)
		{
			To = "";
			AmountBtc = 0;
			foreach (var r in _additionalRecipients)
			{
				r.Dispose();
			}
			_additionalRecipients.Clear();
			IsPrimarySubtractFee = false;
			ClearValidations();

			if (_coinJoinManager is { } coinJoinManager)
			{
				coinJoinManager.WalletEnteredSendWorkflow(_walletModel.Id);
			}
		}

		RxApp.MainThreadScheduler.Schedule(async () => await OnAutoPasteAsync());

		base.OnNavigatedTo(inHistory, disposables);

		if (_parameters.Donate)
		{
			To = Constants.DonationAddress;
			Caption = "Donate to The Wasabi Wallet Developers to continue maintaining the software";
			IsFixedAddress = true;
		}
	}

	protected override void OnNavigatedFrom(bool isInHistory)
	{
		base.OnNavigatedFrom(isInHistory);

		if (!isInHistory && _coinJoinManager is { } coinJoinManager)
		{
			coinJoinManager.WalletLeftSendWorkflow(_wallet);
		}
	}
}
