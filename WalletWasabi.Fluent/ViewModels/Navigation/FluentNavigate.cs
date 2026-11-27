using NBitcoin;
using System.Collections.Generic;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using WalletWasabi.Fluent.ViewModels.AddWallet.Create;
using WalletWasabi.Fluent.ViewModels.CoinControl;
using WalletWasabi.Fluent.ViewModels.Dialogs;
using WalletWasabi.Fluent.ViewModels.Dialogs.Authorization;
using WalletWasabi.Fluent.ViewModels.Dialogs.ReleaseHighlights;
using WalletWasabi.Fluent.ViewModels.Login;
using WalletWasabi.Fluent.ViewModels.Settings;
using WalletWasabi.Fluent.ViewModels.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Advanced;
using WalletWasabi.Fluent.ViewModels.Wallets.Bonds;
using WalletWasabi.Fluent.ViewModels.Wallets.CoinJoinPayment;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History.Details;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History.Features;
using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Wallets.Receive;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Fluent.ViewModels.Wallets.Settings;
using WalletWasabi.Wallets;
using System.Threading.Tasks;

namespace WalletWasabi.Fluent.ViewModels.Navigation;

public delegate Task<string?> ShowQrCodeCameraDialog(RoutableViewModel routableViewModel, Network network);

public partial class FluentNavigate
{
	public FluentNavigate(UiContext uiContext)
	{
		UiContext = uiContext;
	}

	public UiContext UiContext { get; }

	private async Task<string?> ShowQrCodeCameraDialogAsync(RoutableViewModel routableViewModel, Network network)
	{
		return await routableViewModel.Navigate().To().ShowQrCameraDialog(network).GetResultAsync();
	}

	public FluentDialog<string?> ShowQrCameraDialog(Network network, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new ShowQrCameraDialogViewModel(UiContext, network);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<string?>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void TransactionDetails(IWalletModel wallet, RegularTransactionModel model, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new TransactionDetailsViewModel(UiContext, wallet, model), navigationMode);
	}

	public FluentDialog<bool> PasswordAuthDialog(IWalletModel wallet, string continueText = "Continue", NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new PasswordAuthDialogViewModel(UiContext, wallet, continueText);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<bool>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void SpeedUpTransactionDialog(IWalletModel wallet, SpeedupTransaction speedupTransaction, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new SpeedUpTransactionDialogViewModel(UiContext, wallet, speedupTransaction), navigationMode);
	}

	public FluentDialog<bool> ShowErrorDialog(string message, string title, string caption, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new ShowErrorDialogViewModel(UiContext, message, title, caption);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<bool>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void WalletInfo(IWalletModel wallet, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new WalletInfoViewModel(UiContext, wallet), navigationMode);
	}

	public void ConnectToTor(NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ConnectToTorViewModel(UiContext), navigationMode);
	}

	public void ConnectToNode(NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ConnectToNodeViewModel(UiContext), navigationMode);
	}

	public void ConnectToNostr(NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ConnectToNostrViewModel(UiContext), navigationMode);
	}

	public void ConnectToCoordinator(NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ConnectToCoordinatorViewModel(UiContext), navigationMode);
	}

	public void RunNostrRelay(NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new RunNostrRelayViewModel(UiContext), navigationMode);
	}

	public void ConfigureWallet(IWalletModel wallet, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ConfigureWalletViewModel(UiContext, wallet), navigationMode);
	}

	public FluentDialog<IEnumerable<SmartCoin>> SelectCoinsDialog(IWalletModel wallet, IList<CoinModel> selectedCoins, SendFlowModel sendFlow, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new SelectCoinsDialogViewModel(UiContext, wallet, selectedCoins, sendFlow);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<IEnumerable<SmartCoin>>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void TransactionPreview(IWalletModel walletModel, SendFlowModel sendFlow, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new TransactionPreviewViewModel(UiContext, walletModel, sendFlow), navigationMode);
	}

	public void Login(IWalletModel wallet, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new LoginViewModel(UiContext, wallet), navigationMode);
	}

	public FluentDialog<string?> CreatePasswordDialog(string title, string caption = "", bool enableEmpty = true, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new CreatePasswordDialogViewModel(UiContext, title, caption, enableEmpty);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<string?>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public FluentDialog<System.Reactive.Unit> ReleaseHighlightsDialog(NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new ReleaseHighlightsDialogViewModel(UiContext);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<System.Reactive.Unit>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public FluentDialog<FeeRate> CustomFeeRateDialog(TransactionInfo transactionInfo, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new CustomFeeRateDialogViewModel(UiContext, transactionInfo);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<FeeRate>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void SendSuccess(SmartTransaction finalTransaction, string? title = null, string? caption = null, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new SendSuccessViewModel(UiContext, finalTransaction, title, caption), navigationMode);
	}

	public FluentDialog<(int MinGapLimit, uint BirthHeight)?> AdvancedRecoveryOptions(int minGapLimit, uint birthHeight, uint earliestBirthHeight, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new AdvancedRecoveryOptionsViewModel(UiContext, minGapLimit, birthHeight, earliestBirthHeight);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<(int, uint)?>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void RecoverMultiShareWallet(WalletCreationOptions.RecoverWallet options, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new RecoverMultiShareWalletViewModel(UiContext, options), navigationMode);
	}

	public void CancelTransactionDialog(IWalletModel wallet, CancellingTransaction cancellingTransaction, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new CancelTransactionDialogViewModel(UiContext, wallet, cancellingTransaction), navigationMode);
	}

	public FluentDialog<ResyncWalletDialogResult?> ResyncWallet(uint birthHeight, int minGapLimit, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new ResyncWalletViewModel(UiContext, birthHeight, minGapLimit);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<ResyncWalletDialogResult?>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	/// <summary>
	/// Shows the settings. As long as there is no wallet they are the welcome. The result tells how they were left:
	/// they close for what is not one of their pages.
	/// </summary>
	public FluentDialog<SettingsExit> SettingsPage(NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new SettingsPageViewModel(UiContext);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<SettingsExit>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void ConfirmMultiShare(WalletCreationOptions.AddNewWallet options, Dictionary<int, List<RecoveryWordViewModel>> wordsDictionary, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ConfirmMultiShareViewModel(UiContext, options, wordsDictionary), navigationMode);
	}

	public void AddCoinJoinPayment(IWalletModel walletModel, Wallet wallet, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var viewModel = new AddCoinJoinPaymentViewModel(UiContext, walletModel, wallet, ShowQrCodeCameraDialogAsync);
		UiContext.Navigate(navigationTarget).To(viewModel, navigationMode);
	}

	public void MultiShare(WalletCreationOptions.AddNewWallet options, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new MultiShareViewModel(UiContext, options), navigationMode);
	}

	public FluentDialog<bool> ConfirmHideAddress(string address, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new ConfirmHideAddressViewModel(UiContext, address);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<bool>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	/// <summary>
	/// Shows an address that awaits its payment: one that was just generated, or one that was generated earlier.
	/// </summary>
	public void Receive(IWalletModel wallet, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ReceiveViewModel(UiContext, wallet), navigationMode);
	}

	public void ReceiveAddress(IWalletModel wallet, IAddress model, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ReceiveAddressViewModel(UiContext, wallet, model), navigationMode);
	}

	public void ShuttingDown(ApplicationViewModel applicationViewModel, bool restart, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new ShuttingDownViewModel(UiContext, applicationViewModel, restart), navigationMode);
	}

	public void CoinJoinDetails(IWalletModel wallet, CoinJoinTransactionModel transaction, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new CoinJoinDetailsViewModel(UiContext, wallet, transaction), navigationMode);
	}

	/// <summary>Asks whether to lock the fidelity bond that the question describes.</summary>
	public FluentDialog<bool> ConfirmBond(BondQuote quote, NavigationTarget navigationTarget = NavigationTarget.CompactDialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new ConfirmBondViewModel(UiContext, quote);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<bool>(target.NavigateDialogAsync(dialog, navigationMode));
	}

	public void RedeemBond(IWalletModel walletModel, FidelityBondModel bond, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new RedeemBondViewModel(UiContext, walletModel, bond), navigationMode);
	}

	public void AddedWalletPage(WalletSettingsModel walletSettings, WalletCreationOptions options, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new AddedWalletPageViewModel(UiContext, walletSettings, options), navigationMode);
	}

	public void MultiShareOptions(WalletCreationOptions.AddNewWallet options, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new MultiShareOptionsViewModel(UiContext, options), navigationMode);
	}

	public void Wallet(IWalletModel walletModel, Wallet wallet, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new WalletViewModel(UiContext, walletModel, wallet), navigationMode);
	}

	public void CoinJoinsDetails(IWalletModel wallet, CoinJoinTransactionGroupModel transaction, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		UiContext.Navigate(navigationTarget).To(new CoinJoinsDetailsViewModel(UiContext, wallet, transaction), navigationMode);
	}

	public FluentDialog<FeeRate> SendFee(Wallet wallet, TransactionInfo transactionInfo, bool isSilent, NavigationTarget navigationTarget = NavigationTarget.DialogScreen, NavigationMode navigationMode = NavigationMode.Normal)
	{
		var dialog = new SendFeeViewModel(UiContext, wallet, transactionInfo, isSilent);
		var target = UiContext.Navigate(navigationTarget);
		target.To(dialog, navigationMode);

		return new FluentDialog<FeeRate>(target.NavigateDialogAsync(dialog, navigationMode));
	}

}

