using System.Collections.Generic;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>The Tor tab of the settings: whether Tor is used, and whether it is stopped with the application.</summary>
[AppLifetime]
public class TorSettingsTabViewModel : ViewModelBase
{
	public TorSettingsTabViewModel(UiContext uiContext, ApplicationSettings settings) : base(uiContext)
	{
		Settings = settings;
	}

	public bool IsReadOnly => Settings.IsOverridden;

	public ApplicationSettings Settings { get; }

	public IReadOnlyList<TorMode> TorModes =>
		Enum.GetValues<TorMode>();
}
