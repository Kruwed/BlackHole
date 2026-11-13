using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Settings;

[AppLifetime]
[NavigationMetaData(
	Title = "Connections",
	Caption = "Manage connections settings",
	Order = 3,
	Category = "Settings",
	Keywords = new[]
	{
		"Settings", "Connections", "URI", "Provider", "Fee", "Estimation", "Network", "Anonymization",
		"Tor", "Terminate", "Wasabi", "Shut", "Reset"
	},
	IconName = "settings_general_regular")]
public partial class ConnectionsSettingsTabViewModel : RoutableViewModel
{
	public ConnectionsSettingsTabViewModel(UiContext uiContext, ApplicationSettings settings) : base(uiContext)
	{
		Settings = settings;

		if (settings.Network == Network.Main)
		{
			ExternalBroadcastProviders = ExternalTransactionBroadcaster.Providers.Select(x => x.Name);
		}
		else
		{
			ExternalBroadcastProviders = ExternalTransactionBroadcaster.TestNet4Providers.Select(x => x.Name);
		}
	}

	public bool IsReadOnly => Settings.IsOverridden;

	public ApplicationSettings Settings { get; }

	public ImmutableArray<string> FeeRateEstimationProviders => FeeRateProviders.Providers;
	public IEnumerable<string> ExternalBroadcastProviders { get; }

	public IReadOnlyList<TorMode> TorModes =>
		Enum.GetValues<TorMode>();
}
