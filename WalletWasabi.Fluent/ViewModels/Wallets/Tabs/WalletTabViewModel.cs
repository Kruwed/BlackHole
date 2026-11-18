using System.Windows.Input;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// One of the pages of the wallet. The title bar lists the pages by their titles, as tabs, and the window shows the one that is selected.
/// </summary>
public abstract partial class WalletTabViewModel : ActivatableViewModel
{
	[AutoNotify(SetterModifier = AccessModifier.Internal)] private bool _isSelected;

	/// <param name="select">Shows the given tab in the window.</param>
	protected WalletTabViewModel(UiContext uiContext, string title, Action<WalletTabViewModel> select) : base(uiContext)
	{
		Title = title;
		SelectCommand = ReactiveCommand.Create(() => select(this));
	}

	/// <summary>What the tab is called in the title bar.</summary>
	public string Title { get; }

	/// <summary>Shows this tab. This is what a click on its title does.</summary>
	public ICommand SelectCommand { get; }
}
