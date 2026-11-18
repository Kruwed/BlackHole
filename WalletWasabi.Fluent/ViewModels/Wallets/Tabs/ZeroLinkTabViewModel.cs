using WalletWasabi.Fluent.Infrastructure;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>The tab of the ZeroLink coinjoins. It is a placeholder: the wallet does nothing of that protocol yet.</summary>
[AppLifetime]
public class ZeroLinkTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "ZeroLink";

	public ZeroLinkTabViewModel(UiContext uiContext, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
	}
}
