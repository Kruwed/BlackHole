using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views.Wallets.Tabs;

public class JoinMarketTabView : UserControl
{
	public JoinMarketTabView()
	{
		InitializeComponent();
	}

	private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
