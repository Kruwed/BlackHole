using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using Constants = WalletWasabi.Helpers.Constants;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Bonds;

/// <summary>
/// The amount of a new fidelity bond: how much of the coins that the user picked goes to the address of the bond. The
/// transaction is previewed and sent like any other payment after that.
/// </summary>
[NavigationMetaData(Title = "Lock Coins", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class BondAmountViewModel : RoutableViewModel
{
	private readonly IWalletModel _walletModel;
	private readonly SendFlowModel _parameters;
	private readonly uint _unlockHeight;
	private readonly CoinJoinManager? _coinJoinManager;

	[AutoNotify] private decimal? _amountBtc;

	/// <param name="parameters">The coins that the user picked, and the block height that they are locked until.</param>
	public BondAmountViewModel(UiContext uiContext, IWalletModel walletModel, SendFlowModel parameters) : base(uiContext)
	{
		_walletModel = walletModel;
		_parameters = parameters;
		_unlockHeight = parameters.BondUnlockHeight ?? throw new ArgumentException("The block height that the coins are locked until is missing.", nameof(parameters));
		_coinJoinManager = UiContext.Services.GetHostedService<CoinJoinManager>();

		Balance = new Amount(parameters.AvailableAmount);
		Address = walletModel.Bonds.GetAddress(_unlockHeight).ToString();
		UnlockText = CreateBondViewModel.DescribeLock(walletModel, _unlockHeight);

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: false);
		EnableBack = true;

		this.ValidateProperty(x => x.AmountBtc, ValidateAmount);

		InsertMaxCommand = ReactiveCommand.Create(() => { AmountBtc = _parameters.AvailableAmountBtc; });

		var canContinue = this.WhenAnyValue(x => x.AmountBtc).Select(amount => amount is > 0 && amount <= _parameters.AvailableAmountBtc);
		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync, canContinue);
	}

	/// <summary>What the coins that the user picked hold. No more than that can be locked.</summary>
	public Amount Balance { get; }

	public string Address { get; }

	public string UnlockText { get; }

	public ICommand InsertMaxCommand { get; }

	private void ValidateAmount(IValidationErrors errors)
	{
		if (AmountBtc is not { } amount)
		{
			return;
		}

		if (amount > Constants.MaximumNumberOfBitcoins)
		{
			errors.Add(ErrorSeverity.Error, "Amount must be less than the total supply of BTC.");
		}
		else if (amount <= 0)
		{
			errors.Add(ErrorSeverity.Error, "Amount must be more than 0 BTC");
		}
		else if (amount > _parameters.AvailableAmountBtc)
		{
			errors.Add(ErrorSeverity.Error, "The coins that you picked do not hold that much.");
		}
	}

	private async Task OnNextAsync()
	{
		if (AmountBtc is not { } amountBtc)
		{
			return;
		}

		var amount = new Money(amountBtc, MoneyUnit.BTC);
		var destination = new Destination.Loudly(_walletModel.Bonds.GetAddress(_unlockHeight).ScriptPubKey);

		var transactionInfo = new TransactionInfo(destination, _walletModel.Settings.AnonScoreTarget)
		{
			Amount = amount,

			// When everything is locked, the bond pays the mining fee.
			SubtractFee = amount == _parameters.AvailableAmount
		};

		// The coinjoin that is under way stops, so that the transaction can spend the coins.
		if (_coinJoinManager is { } coinJoinManager)
		{
			await coinJoinManager.WalletEnteredSendingAsync(_parameters.Wallet);
		}

		Navigate().To().TransactionPreview(_walletModel, _parameters with { TransactionInfo = transactionInfo });
	}

	// Locking coins is spending them. The coinjoins of the wallet wait for it like they wait for a payment.
	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		if (!isInHistory && _coinJoinManager is { } coinJoinManager)
		{
			coinJoinManager.WalletEnteredSendWorkflow(_walletModel.Id);
		}

		base.OnNavigatedTo(isInHistory, disposables);
	}

	protected override void OnNavigatedFrom(bool isInHistory)
	{
		base.OnNavigatedFrom(isInHistory);

		if (!isInHistory && _coinJoinManager is { } coinJoinManager)
		{
			coinJoinManager.WalletLeftSendWorkflow(_parameters.Wallet);
		}
	}
}
