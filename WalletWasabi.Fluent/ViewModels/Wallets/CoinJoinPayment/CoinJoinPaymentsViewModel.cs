using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Windows.Input;
using Avalonia.Threading;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.CoinJoinPayment;

/// <summary>
/// The payments that wait for a WabiSabi round to make them: the ones that are queued, the ones that a round is making
/// and the ones that were made, with the way to queue another one. It is a part of the main screen of WabiSabi in the
/// Coinjoin dialog.
/// </summary>
public partial class CoinJoinPaymentsViewModel : ViewModelBase
{
	private readonly Wallet _wallet;
	private readonly IWalletModel _walletModel;
	private readonly DispatcherTimer _refreshTimer;

	[AutoNotify] private ObservableCollection<CoinJoinPaymentViewModel> _pendingPayments = new();
	[AutoNotify] private ObservableCollection<CoinJoinPaymentViewModel> _inProgressPayments = new();
	[AutoNotify] private ObservableCollection<CoinJoinPaymentViewModel> _completedPayments = new();
	[AutoNotify] private bool _hasAnyPayments;
	[AutoNotify] private bool _hasPendingPayments;
	[AutoNotify] private bool _hasInProgressPayments;
	[AutoNotify] private bool _hasCompletedPayments;
	[AutoNotify] private int _totalPaymentCount;

	public CoinJoinPaymentsViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet) : base(uiContext)
	{
		_wallet = wallet;
		_walletModel = walletModel;

		AddPaymentCommand = ReactiveCommand.Create(OnAddPayment);
		CancelPaymentCommand = ReactiveCommand.Create<CoinJoinPaymentViewModel>(OnCancelPayment);

		_refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		_refreshTimer.Tick += (_, _) => RefreshPayments();
	}

	/// <summary>Opens the page that queues a payment. The way back from there leads to the dialog.</summary>
	public ICommand AddPaymentCommand { get; }

	public ICommand CancelPaymentCommand { get; }

	/// <summary>
	/// Shows the payments as they are now, and keeps them up to date until what is given is disposed of: a round changes
	/// them without telling.
	/// </summary>
	public void Activate(CompositeDisposable disposables)
	{
		RefreshPayments();
		_refreshTimer.Start();

#pragma warning disable CA2000 // Dispose objects before losing scope
		Disposable.Create(() => _refreshTimer.Stop())
			.DisposeWith(disposables);
#pragma warning restore CA2000 // Dispose objects before losing scope
	}

	private void OnAddPayment()
	{
		UiContext.Navigate(NavigationTarget.DialogScreen).To().AddCoinJoinPayment(_walletModel, _wallet);
	}

	private void OnCancelPayment(CoinJoinPaymentViewModel payment)
	{
		try
		{
			_wallet.CancelCoinJoinPayment(payment.Id);
			RefreshPayments();
		}
		catch (InvalidOperationException)
		{
			// Payment is no longer pending or was not found
			RefreshPayments();
		}
	}

	private void RefreshPayments()
	{
		var payments = _wallet.BatchedPayments.GetPayments();
		var network = _walletModel.Network;

		var pending = new ObservableCollection<CoinJoinPaymentViewModel>();
		var inProgress = new ObservableCollection<CoinJoinPaymentViewModel>();
		var completed = new ObservableCollection<CoinJoinPaymentViewModel>();

		foreach (var payment in payments)
		{
			var vm = new CoinJoinPaymentViewModel(UiContext, payment, network);
			if (vm.IsPending)
			{
				pending.Add(vm);
			}
			else if (vm.IsInProgress)
			{
				inProgress.Add(vm);
			}
			else if (vm.IsFinished)
			{
				completed.Add(vm);
			}
		}

		PendingPayments = pending;
		InProgressPayments = inProgress;
		CompletedPayments = completed;

		HasPendingPayments = pending.Count > 0;
		HasInProgressPayments = inProgress.Count > 0;
		HasCompletedPayments = completed.Count > 0;
		HasAnyPayments = payments.Count > 0;
		TotalPaymentCount = payments.Count;
	}
}
