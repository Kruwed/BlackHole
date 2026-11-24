using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Market;

/// <summary>
/// The question that is asked before coins are locked in a fidelity bond: how much, for how long, and at what fee.
/// A click beside it or Escape gives the bond up.
/// </summary>
public partial class ConfirmBondViewModel : DialogViewModelBase<bool>
{
	public ConfirmBondViewModel(UiContext uiContext, BondQuote quote) : base(uiContext)
	{
		Title = quote.Title;
		LockText = quote.LockText;
		FeeText = quote.FeeText;

		NextCommand = ReactiveCommand.Create(() => Close(result: true));
		CancelCommand = ReactiveCommand.Create(() => Close(DialogResultKind.Cancel));

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);
	}

	/// <summary>"Confirm Bond for 1.20000000 BTC".</summary>
	public override string Title { get; protected set; }

	/// <summary>"Locked for 2,016 blocks (2 weeks)".</summary>
	public string LockText { get; }

	/// <summary>"Fee rate 1 sat/vByte (154 sats)".</summary>
	public string FeeText { get; }
}
