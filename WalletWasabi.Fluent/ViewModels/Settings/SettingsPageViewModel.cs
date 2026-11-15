using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;

namespace WalletWasabi.Fluent.ViewModels.Settings;

[AppLifetime]
[NavigationMetaData(Title = "Settings", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class SettingsPageViewModel : DialogViewModelBase<Unit>
{
	[AutoNotify] private bool _isModified;
	[AutoNotify] private int _selectedTab;

	public SettingsPageViewModel(UiContext uiContext) : base(uiContext)
	{
		_selectedTab = 0;

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		ConnectionsSettingsTab = new ConnectionsSettingsTabViewModel(UiContext, UiContext.ApplicationSettings);
		CoordinatorTabSettings = new CoordinatorTabSettingsViewModel(UiContext, UiContext.ApplicationSettings);

		RestartCommand = ReactiveCommand.Create(() => AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true));
		NextCommand = ReactiveCommand.Create(() => Close());
		OpenCommand = ReactiveCommand.CreateFromTask(Activate);

		// Show restart message when needed
		UiContext.ApplicationSettings.IsRestartNeeded
									 .BindTo(this, x => x.IsModified);

		// Show restart notification when needed only if this page is not active.
		UiContext.ApplicationSettings.IsRestartNeeded
				 .Where(x => x && !IsActive)
				 .Do(_ => NotificationHelpers.Show(new RestartViewModel(UiContext, "To apply the new setting, Black Hole needs to be restarted")))
				 .Subscribe();
	}

	public bool IsReadOnly => UiContext.ApplicationSettings.IsOverridden;

	public ICommand RestartCommand { get; }

	/// <summary>Opens the settings. This is what the button in the title bar does.</summary>
	public ICommand OpenCommand { get; }

	public ConnectionsSettingsTabViewModel ConnectionsSettingsTab { get; }
	public CoordinatorTabSettingsViewModel CoordinatorTabSettings { get; }

	public async Task Activate()
	{
		await NavigateDialogAsync(this);
	}

	public async Task ActivateCoordinatorTabAsync()
	{
		SelectedTab = 1;
		await NavigateDialogAsync(this);
	}
}
