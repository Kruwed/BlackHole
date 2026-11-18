using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

[NavigationMetaData(Title = "Select Coins", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class ManualControlDialogViewModel : DialogViewModelBase<IEnumerable<SmartCoin>>
{
	[AutoNotify] private bool _hasSelection;

	private readonly IWalletModel _walletModel;
	private readonly Wallet _wallet;
	private readonly uint? _bondUnlockHeight;

	/// <param name="bondUnlockHeight">The block height that the coins are locked until, if they are picked for a fidelity bond and not for a payment.</param>
	public ManualControlDialogViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, uint? bondUnlockHeight = null) : base(uiContext)
	{
		_bondUnlockHeight = bondUnlockHeight;

		CoinList = new CoinListViewModel(uiContext, walletModel.Coins, [], allowCoinjoiningCoinSelection: true, allowSelection: true);

		var nextCommandCanExecute =
			CoinList.Selection
					.ToObservableChangeSet()
					.ToCollection()
					.Select(c => c.Count > 0);

		NextCommand = ReactiveCommand.Create(OnNext, nextCommandCanExecute);

		SelectedAmount =
			CoinList.Selection
					.ToObservableChangeSet()
					.ToCollection()
					.Select(c => c.Any() ? new Amount(c.TotalAmount()) : null);

		ToggleSelectionCommand = ReactiveCommand.Create(() => SelectAll(!CoinList.Selection.Any()));

		SetupCancel(true, true, true);

		// The coins of a payment are the first thing to pick. The coins of a bond come after its unlock height, and the way back there is open.
		EnableBack = bondUnlockHeight is not null;

		_walletModel = walletModel;
		_wallet = wallet;
	}

	public CoinListViewModel CoinList { get; }

	public string Caption => _bondUnlockHeight is null
		? "Select the coins that the transaction may spend"
		: "Select the coins to lock in the fidelity bond";

	public ICommand ToggleSelectionCommand { get; }

	public IObservable<Amount?> SelectedAmount { get; }

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		CoinList.CoinItems
				.ToObservableChangeSet()
				.WhenPropertyChanged(x => x.IsSelected)
				.Select(_ => CoinList.Selection.Count > 0)
				.BindTo(this, x => x.HasSelection)
				.DisposeWith(disposables);
	}

	protected override void OnNavigatedFrom(bool isInHistory)
	{
		if (!isInHistory)
		{
			CoinList.Dispose();
		}
	}

	private void OnNext()
	{
		var coins = CoinList.Selection.GetSmartCoins().ToList();

		var sendParameters = new SendFlowModel(_wallet, _walletModel, coins, UiContext.Services) { BondUnlockHeight = _bondUnlockHeight };

		if (_bondUnlockHeight is not null)
		{
			Navigate().To().BondAmount(_walletModel, sendParameters);
		}
		else
		{
			Navigate().To().Send(_walletModel, sendParameters);
		}
	}

	private void SelectAll(bool value)
	{
		foreach (var coin in CoinList.CoinItems)
		{
			coin.IsSelected = value;
		}
	}
}
