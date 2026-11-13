using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Logging;
using WalletWasabi.Services;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

[NavigationMetaData(
	Title = "Send",
	Caption = "",
	IconName = "wallet_action_send",
	NavBarPosition = NavBarPosition.None,
	Searchable = false,
	NavigationTarget = NavigationTarget.DialogScreen)]
public partial class SendFeeViewModel : DialogViewModelBase<FeeRate>
{
	private readonly Wallet _wallet;
	private readonly TransactionInfo _transactionInfo;
	private readonly bool _isSilent;

	[AutoNotify] private IReadOnlyList<FeeRateOption> _options = [];
	[AutoNotify] private FeeRateOption? _selectedOption;

	public SendFeeViewModel(UiContext uiContext, Wallet wallet, TransactionInfo transactionInfo, bool isSilent) : base(uiContext)
	{
		_isSilent = isSilent;
		IsBusy = isSilent;
		_wallet = wallet;
		_transactionInfo = transactionInfo;

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: false, escapeGoesBack: true);
		EnableBack = true;

		NextCommand = ReactiveCommand.Create(OnNext, this.WhenAnyValue(x => x.SelectedOption).Select(x => x is not null));

		AdvancedOptionsCommand = ReactiveCommand.CreateFromTask(ShowAdvancedOptionsAsync);
	}

	public ICommand AdvancedOptionsCommand { get; }

	private void OnNext()
	{
		if (SelectedOption is not { } option)
		{
			return;
		}

		UiContext.Services.SetFeeTarget(option.BlockTarget);
		Close(DialogResultKind.Normal, option.FeeRate);
	}

	/// <summary>
	/// Rebuilds the presets from the given estimations. Returns false and keeps the current presets if none is usable.
	/// </summary>
	private bool TryUpdateOptions(FeeRateEstimations estimations)
	{
		var options = FeeRateOption.FromEstimations(estimations, _transactionInfo.MaximumPossibleFeeRate);
		if (options.Count == 0)
		{
			return false;
		}

		// Keep the user's selection across refreshes; otherwise start from the fee rate already in use or the remembered target.
		var selected = SelectedOption is { } current
			? FeeRateOption.SelectDefault(options, currentFeeRate: null, current.BlockTarget)
			: FeeRateOption.SelectDefault(options, _transactionInfo.FeeRate, UiContext.Services.GetFeeTarget());

		Options = options;
		SelectedOption = selected;
		return true;
	}

	private async Task ShowAdvancedOptionsAsync()
	{
		var result = await ShowCustomFeeRateDialogAsync();
		if (result is { } feeRate && feeRate != FeeRate.Zero)
		{
			Close(DialogResultKind.Normal, feeRate);
		}
	}

	private async Task<FeeRate?> ShowCustomFeeRateDialogAsync()
	{
		var result = await NavigateDialogAsync(new CustomFeeRateDialogViewModel(UiContext, _transactionInfo), NavigationTarget.CompactDialogScreen);
		return result.Result;
	}

	private async Task FeeEstimationsAreNotAvailableAsync()
	{
		await ShowErrorAsync(
			"Transaction fee",
			"Transaction fee estimations are not available at the moment. Try again later or you can enter the fee rate manually.",
			"",
			NavigationTarget.CompactDialogScreen);

		var customFeeRate = await ShowCustomFeeRateDialogAsync();

		if (customFeeRate is { })
		{
			Close(DialogResultKind.Normal, customFeeRate);
		}
		else
		{
			Close();
		}
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		IsBusy = true;

		base.OnNavigatedTo(isInHistory, disposables);

		UiContext.Services.EventBus.AsObservable<MiningFeeRatesChanged>()
			.Select(e =>
			{
				TransactionFeeHelper.TryGetFeeEstimates(e.AllFeeEstimate, _wallet.Network, out var estimates);
				return estimates;
			})
			.WhereNotNull()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(estimations => TryUpdateOptions(estimations))
			.DisposeWith(disposables);

		RxApp.MainThreadScheduler.Schedule(async () =>
		{
			if (!TransactionFeeHelper.TryGetFeeEstimates(_wallet, out var feeRateEstimations) ||
				!TryUpdateOptions(feeRateEstimations))
			{
				Logger.LogInfo("Transaction fee estimations are not available.");
				await FeeEstimationsAreNotAvailableAsync();
				return;
			}

			if (_isSilent)
			{
				OnNext();
			}
			else
			{
				IsBusy = false;
			}
		});
	}
}
