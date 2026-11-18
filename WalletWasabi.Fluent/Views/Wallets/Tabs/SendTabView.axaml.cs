using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views.Wallets.Tabs;

public class SendTabView : UserControl
{
	public SendTabView()
	{
		InitializeComponent();
	}

	private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
