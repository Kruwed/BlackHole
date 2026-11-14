using System.Reactive.Linq;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Notifications;

[AppLifetime]
public partial class WalletNotificationsViewModel : ViewModelBase
{
	private readonly Func<IWalletViewModel?> _getWalletViewModel;
	[AutoNotify] private bool _isBusy;

	/// <param name="getWalletViewModel">Gives the view model of the wallet, once the wallet is loaded.</param>
	public WalletNotificationsViewModel(UiContext uiContext, Func<IWalletViewModel?> getWalletViewModel) : base(uiContext)
	{
		_getWalletViewModel = getWalletViewModel;
	}

	public void StartListening()
	{
		UiContext.WalletRepository
			.WhenAnyValue(x => x.Wallet)
			.WhereNotNull()
			.Take(1)
			.SelectMany(wallet => wallet.Transactions.NewTransactionArrived.Where(_ => wallet is { IsLoggedIn: true, IsLoaded: true }))
			.Where(x => !UiContext.ApplicationSettings.PrivacyMode)
			.Where(x => x.EventArgs.IsNews)
			.DoAsync(x => OnNotificationReceivedAsync(x.Wallet, x.EventArgs))
			.Subscribe();
	}

	private async Task OnNotificationReceivedAsync(IWalletModel wallet, ProcessedResult e)
	{
		if (!e.IsOwnCoinJoin)
		{
			void OnClick()
			{
				if (UiContext.Navigate().IsAnyPageBusy)
				{
					return;
				}

				_getWalletViewModel()?.SelectTransaction(e.Transaction.GetHash());
			}

			NotificationHelpers.Show(e, OnClick);
		}

		if (e.NewlyReceivedCoins.Count != 0 || e.NewlyConfirmedReceivedCoins.Count != 0)
		{
			await Task.Delay(200);
			_getWalletViewModel()?.SelectTransaction(e.Transaction.GetHash());
		}
	}
}
