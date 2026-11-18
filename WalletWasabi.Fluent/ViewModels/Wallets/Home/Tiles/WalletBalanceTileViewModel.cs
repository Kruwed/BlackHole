using System.Reactive.Linq;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Home.Tiles;

public class WalletBalanceTileViewModel : ActivatableViewModel
{
	/// <param name="amounts">What the wallet owns, the coins in its fidelity bonds included.</param>
	/// <param name="bondAmounts">The part of it that is locked in fidelity bonds.</param>
	public WalletBalanceTileViewModel(UiContext uiContext, IObservable<Amount> amounts, IObservable<Amount> bondAmounts) : base(uiContext)
	{
		Amounts = amounts;
		BondNotes = bondAmounts.Select(DescribeBonds);
	}

	public IObservable<Amount> Amounts { get; }

	/// <summary>Tells how much of the balance is locked, for whoever wonders why it cannot all be sent. Null without bonds.</summary>
	public IObservable<string?> BondNotes { get; }

	public static string? DescribeBonds(Amount bondAmount) =>
		bondAmount.HasBalance
			? $"{bondAmount.FormattedBtc} BTC of it is locked in fidelity bonds."
			: null;
}
