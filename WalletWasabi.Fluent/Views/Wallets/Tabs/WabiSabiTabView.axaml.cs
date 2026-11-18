using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views.Wallets.Tabs;

public class WabiSabiTabView : UserControl
{
	public WabiSabiTabView()
	{
		InitializeComponent();
	}

	private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
