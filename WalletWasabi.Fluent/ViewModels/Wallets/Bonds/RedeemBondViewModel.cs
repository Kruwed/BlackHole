using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Dialogs.Authorization;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Bonds;

/// <summary>
/// Redeems a fidelity bond whose locktime is over: its coins go back to the wallet, to a new address of it. The bond pays
/// the mining fee, and the user chooses how much that is.
/// </summary>
[NavigationMetaData(Title = "Redeem Fidelity Bond", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class RedeemBondViewModel : RoutableViewModel
{
	private readonly IWalletModel _walletModel;
	private readonly FidelityBondModel _bond;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<FeeRateOption> _options = [];
	[AutoNotify] private FeeRateOption? _selectedOption;
	[AutoNotify] private string _feeRateText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private Amount? _fee;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private Amount? _received;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _error;

	public RedeemBondViewModel(UiContext uiContext, IWalletModel walletModel, FidelityBondModel bond) : base(uiContext)
	{
		_walletModel = walletModel;
		_bond = bond;

		BondAmount = new Amount(bond.Bond.Amount);
		UnlockDate = BondItemViewModel.FormatDate(bond.Bond.Locktime.Date);

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: false);
		EnableBack = true;

		this.ValidateProperty(x => x.FeeRateText, ValidateFeeRate);

		// An estimation that is picked fills the fee rate in. A fee rate that is typed is no estimation any more.
		this.WhenAnyValue(x => x.SelectedOption)
			.WhereNotNull()
			.Subscribe(option => FeeRateText = Format(option.FeeRate));

		this.WhenAnyValue(x => x.FeeRateText)
			.Subscribe(text =>
			{
				if (SelectedOption is { } option && Format(option.FeeRate) != text)
				{
					SelectedOption = null;
				}

				UpdatePreview();
			});

		NextCommand = ReactiveCommand.CreateFromTask(OnRedeemAsync, this.WhenAnyValue(x => x.Received).Select(received => received is not null));
	}

	public Amount BondAmount { get; }

	public string UnlockDate { get; }

	/// <summary>
	/// The estimation to start with: the one that the user had picked, by its number of blocks, or else the one nearest to
	/// the number of blocks that the user picked the last time for a payment.
	/// </summary>
	public static FeeRateOption? SelectOption(IReadOnlyList<FeeRateOption> options, FeeRateOption? picked, int preferredBlockTarget) =>
		FeeRateOption.SelectDefault(options, currentFeeRate: null, picked?.BlockTarget ?? preferredBlockTarget);

	/// <summary>Reads a fee rate in satoshis per virtual byte. Null if it is not one that a transaction can pay.</summary>
	public static FeeRate? ParseFeeRate(string text)
	{
		if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
		{
			return null;
		}

		if (value < Constants.MinRelayFeeRate.SatoshiPerByte || value > Constants.AbsurdlyHighFeeRate.SatoshiPerByte)
		{
			return null;
		}

		return new FeeRate(value);
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		UiContext.Services.EventBus.AsObservable<MiningFeeRatesChanged>()
			.Select(e => e.AllFeeEstimate)
			.StartWith(_walletModel.Bonds.FeeRateEstimations)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(UpdateOptions)
			.DisposeWith(disposables);
	}

	private static string Format(FeeRate feeRate) => feeRate.SatoshiPerByte.ToString("0.##", CultureInfo.InvariantCulture);

	private void UpdateOptions(FeeRateEstimations? estimations)
	{
		if (!TransactionFeeHelper.TryGetFeeEstimates(estimations, _walletModel.Network, out var usable))
		{
			return;
		}

		// A fee rate that the user typed stays when the estimations of the node change.
		var isTyped = SelectedOption is null && FeeRateText != "";

		Options = FeeRateOption.FromEstimations(usable);

		if (!isTyped)
		{
			SelectedOption = SelectOption(Options, SelectedOption, UiContext.Services.GetFeeTarget());
		}
	}

	private void ValidateFeeRate(IValidationErrors errors)
	{
		if (FeeRateText == "" || ParseFeeRate(FeeRateText) is not null)
		{
			return;
		}

		if (!decimal.TryParse(FeeRateText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
		{
			errors.Add(ErrorSeverity.Error, "The entered fee rate is not valid.");
		}
		else if (value < Constants.MinRelayFeeRate.SatoshiPerByte)
		{
			errors.Add(ErrorSeverity.Error, $"Cannot be less than {Constants.MinRelayFeeRate.SatoshiPerByte} sat/vByte.");
		}
		else
		{
			errors.Add(ErrorSeverity.Error, $"Cannot be more than {Constants.AbsurdlyHighFeeRate.SatoshiPerByte:0} sat/vByte.");
		}
	}

	/// <summary>
	/// Builds the transaction for the fee rate to show what it costs. Nothing is sent before the user confirms.
	/// </summary>
	private void UpdatePreview()
	{
		Fee = null;
		Received = null;
		Error = null;

		if (ParseFeeRate(FeeRateText) is not { } feeRate)
		{
			return;
		}

		try
		{
			var transaction = _walletModel.Bonds.BuildRedemption(_bond.Bond, feeRate);
			var received = transaction.Transaction.Outputs[0].Value;

			Fee = new Amount(_bond.Bond.Amount - received);
			Received = new Amount(received);
		}
		catch (InvalidOperationException ex)
		{
			Error = ex.Message;
		}
	}

	private async Task OnRedeemAsync()
	{
		if (ParseFeeRate(FeeRateText) is not { } feeRate)
		{
			return;
		}

		try
		{
			if (!await AuthorizeAsync())
			{
				return;
			}

			IsBusy = true;

			var transaction = await Task.Run(() => _walletModel.Bonds.BuildRedemption(_bond.Bond, feeRate));
			await UiContext.Services.SendTransactionAsync(transaction);

			if (SelectedOption is { } option)
			{
				UiContext.Services.SetFeeTarget(option.BlockTarget);
			}

			Navigate().To().SendSuccess(transaction, "Bond redeemed", "The coins of the fidelity bond are on their way back to the wallet.");
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			await ShowErrorAsync("Redeem Fidelity Bond", ex.ToUserFriendlyString(), "Black Hole was unable to redeem the fidelity bond.");
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task<bool> AuthorizeAsync()
	{
		if (!_walletModel.Auth.HasPassword)
		{
			return true;
		}

		var authDialog = new PasswordAuthDialogViewModel(UiContext, _walletModel, "Redeem");
		var result = await NavigateDialogAsync(authDialog, authDialog.DefaultTarget, NavigationMode.Clear);

		return result.Result;
	}
}
