using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.StatusIcon;

[AppLifetime]
public partial class StatusIconViewModel : ViewModelBase
{
	public StatusIconViewModel(UiContext uiContext) : base(uiContext)
	{
		HealthMonitor = uiContext.HealthMonitor;
	}

	public HealthMonitor HealthMonitor { get; }

	public string BitcoinCoreName => "Bitcoin Node";
}
