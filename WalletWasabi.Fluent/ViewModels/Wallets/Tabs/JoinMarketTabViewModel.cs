using System.Collections.Generic;
using System.Windows.Input;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// The tab of the JoinMarket-style coinjoins. The fidelity bonds are all that the wallet has of that protocol so far,
/// and this tab is the only place that opens them. The list of the market makers is a placeholder: it has its columns
/// and no rows, as the wallet does not look for makers yet.
/// </summary>
[AppLifetime]
public partial class JoinMarketTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "JoinMarket";

	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletLoaded;

	public JoinMarketTabViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		// The bonds are read from the transactions of the wallet, so they wait for the wallet to know them all.
		walletModel.WhenAnyValue(x => x.IsLoaded)
			.BindTo(this, x => x.IsWalletLoaded);

		// The fidelity bonds: coins that are locked until the chain reaches a block height.
		BondCommand = ReactiveCommand.Create(
			() => UiContext.Navigate().To().Bonds(walletModel, wallet),
			this.WhenAnyValue(x => x.IsWalletLoaded));
	}

	/// <summary>
	/// The columns of the list of market makers. The first one names the maker, the others tell what it offers.
	/// </summary>
	public static IReadOnlyList<string> Columns { get; } = ["Market Maker", "Fee Rate", "Spread", "Bid", "Ask", "Liquidity", "Bond"];

	/// <summary>Opens the fidelity bonds of the wallet.</summary>
	public ICommand BondCommand { get; }

	public IReadOnlyList<string> MarketMakerColumns => Columns;
}
