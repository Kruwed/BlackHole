using System.Linq;
using System.Reactive.Linq;
using System.Windows.Input;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// The tab of the coins. It lists the coins of the wallet, and the user ticks the ones that something is to be done
/// with. What is done with them starts in the dialog that the Coinjoin button opens: a payment spends them, a coinjoin
/// gets them, a fidelity bond locks them and an offer offers them to the takers of coinjoins. Receiving starts in
/// that dialog too.
/// </summary>
/// <remarks>
/// The ticks belong to the list and to nothing else. They are not kept anywhere, and whatever takes coins takes the
/// ones that are ticked at the moment it is asked for.
/// </remarks>
[AppLifetime]
public partial class CoinsTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "Coins";

	[AutoNotify] private bool _hasSelection;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletLoaded;

	public CoinsTabViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		CoinList = new CoinListViewModel(uiContext, walletModel.Coins, [], allowCoinjoiningCoinSelection: true, allowSelection: true);

		// What the wallet knows about its coins is not complete until it has processed the blocks of the node, so
		// everything that takes coins waits for that.
		walletModel.WhenAnyValue(x => x.IsLoaded)
			.BindTo(this, x => x.IsWalletLoaded);

		CoinList.Selection
			.ToObservableChangeSet()
			.ToCollection()
			.Select(coins => coins.Count > 0)
			.StartWith(false)
			.Subscribe(hasSelection => HasSelection = hasSelection);

		// The dialog is one and the same whenever it is opened: what was typed into its payment stays, and so does the
		// kind of coinjoin that its box is set to.
		CoinJoin = new CoinJoinViewModel(uiContext, walletModel, wallet, CoinList);

		// The dialog is to be had at any time: an address can be generated while the wallet still processes blocks. What
		// takes coins waits for the wallet inside the dialog.
		CoinJoinCommand = ReactiveCommand.Create(
			() =>
			{
				// The dialog is not opened twice: it may be open already, behind a page that it led to.
				if (CoinJoin.CurrentTarget == NavigationTarget.Default)
				{
					UiContext.Navigate(NavigationTarget.DialogScreen).To(CoinJoin);
				}
			});

		ToggleSelectionCommand = ReactiveCommand.Create(() => CoinList.SelectAll(!CoinList.Selection.Any()));
	}

	/// <summary>
	/// The coins of the wallet. The ones that are ticked are the ones that a payment may spend, that a coinjoin round
	/// gets, that a bond locks and that an offer offers. No coin is ticked before the user ticks it: a coin that
	/// arrives, also out of a coinjoin, is not.
	/// </summary>
	public CoinListViewModel CoinList { get; }

	/// <summary>
	/// The dialog of what is done with the ticked coins, and of receiving: the payment and Receive, the box of the
	/// coinjoins, and through its menu the offer of the wallet as a market maker and its fidelity bonds.
	/// </summary>
	public CoinJoinViewModel CoinJoin { get; }

	/// <summary>Opens the dialog of what is done with the ticked coins, and of receiving.</summary>
	public ICommand CoinJoinCommand { get; }

	/// <summary>Ticks every coin, or none if some are ticked.</summary>
	public ICommand ToggleSelectionCommand { get; }
}
