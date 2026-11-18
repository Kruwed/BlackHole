using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views.Wallets.Tabs;

public class ReceiveTabView : UserControl
{
	public ReceiveTabView()
	{
		InitializeComponent();
	}

	private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
