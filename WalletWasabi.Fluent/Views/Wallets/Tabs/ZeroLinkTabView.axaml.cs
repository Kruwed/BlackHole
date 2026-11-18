using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views.Wallets.Tabs;

public class ZeroLinkTabView : UserControl
{
	public ZeroLinkTabView()
	{
		InitializeComponent();
	}

	private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
