using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>
/// What the application shows as long as there is no wallet. It has five columns: Tor, Node, Nostr, Coordinator and
/// Wallet, each with two buttons. The upper one connects to something that exists, or recovers the wallet; the lower
/// one is for running, or creating, one's own.
/// </summary>
/// <remarks>
/// The user sets them up from left to right: the buttons of a column can be used once the columns before it are done,
/// see <see cref="WelcomeProgress"/>. Running a Tor relay, a Bitcoin node, a Nostr relay or a coordinator holds its
/// place and does nothing yet, so these four buttons cannot be used.
/// </remarks>
[NavigationMetaData(Title = "Welcome to Black Hole")]
public partial class WelcomePageViewModel : DialogViewModelBase<Unit>
{
	private readonly WelcomeProgress _progress;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private WelcomeStep _currentStep;

	public WelcomePageViewModel(UiContext uiContext, WelcomeProgress progress) : base(uiContext)
	{
		_progress = progress;
		_currentStep = progress.CurrentStep;

		SetupCancel(enableCancel: false, enableCancelOnEscape: false, enableCancelOnPressed: false);

		Tor = new WelcomeColumnViewModel(WelcomeStep.Tor);
		Node = new WelcomeColumnViewModel(WelcomeStep.Node);
		Nostr = new WelcomeColumnViewModel(WelcomeStep.Nostr);
		Coordinator = new WelcomeColumnViewModel(WelcomeStep.Coordinator);
		Wallet = new WelcomeColumnViewModel(WelcomeStep.Wallet);
		Columns = [Tor, Node, Nostr, Coordinator, Wallet];

		this.WhenAnyValue(x => x.CurrentStep)
			.Subscribe(current =>
			{
				foreach (var column in Columns)
				{
					column.Update(current);
				}
			});

		ConnectToTorCommand = ReactiveCommand.Create(
			() => Navigate().To().ConnectToTor(() => progress.Complete(WelcomeStep.Tor)),
			IsOpen(WelcomeStep.Tor));

		ConnectToNodeCommand = ReactiveCommand.Create(
			() => Navigate().To().ConnectToNode(() => progress.Complete(WelcomeStep.Node)),
			IsOpen(WelcomeStep.Node));

		ConnectToNostrCommand = ReactiveCommand.Create(
			() => Navigate().To().ConnectToNostr(() => progress.Complete(WelcomeStep.Nostr)),
			IsOpen(WelcomeStep.Nostr));

		ConnectToCoordinatorCommand = ReactiveCommand.Create(
			() => Navigate().To().ConnectToCoordinator(() => progress.Complete(WelcomeStep.Coordinator)),
			IsOpen(WelcomeStep.Coordinator));

		RecoverWalletCommand = ReactiveCommand.Create(OnRecoverWallet, IsOpen(WelcomeStep.Wallet));

		CreateWalletCommand = ReactiveCommand.Create(OnCreateWallet, IsOpen(WelcomeStep.Wallet));
	}

	public WelcomeColumnViewModel Tor { get; }

	public WelcomeColumnViewModel Node { get; }

	public WelcomeColumnViewModel Nostr { get; }

	public WelcomeColumnViewModel Coordinator { get; }

	public WelcomeColumnViewModel Wallet { get; }

	/// <summary>The columns from left to right, which is the order in which they are set up.</summary>
	public IReadOnlyList<WelcomeColumnViewModel> Columns { get; }

	public ICommand ConnectToTorCommand { get; }

	public ICommand ConnectToNodeCommand { get; }

	public ICommand ConnectToNostrCommand { get; }

	public ICommand ConnectToCoordinatorCommand { get; }

	public ICommand RecoverWalletCommand { get; }

	public ICommand CreateWalletCommand { get; }

	private IObservable<bool> IsOpen(WelcomeStep step) =>
		this.WhenAnyValue(x => x.CurrentStep, current => WelcomeProgress.IsOpen(step, current));

	private void OnCreateWallet()
	{
		if (CanAddWallet())
		{
			Navigate().To().MultiShareOptions(new WalletCreationOptions.AddNewWallet().WithNewBackup());
		}
	}

	private void OnRecoverWallet()
	{
		if (CanAddWallet())
		{
			Navigate().To().RecoverMultiShareWallet(new WalletCreationOptions.RecoverWallet());
		}
	}

	/// <summary>
	/// Tells the user right away when a wallet cannot be added, e.g. because a wallet file that cannot be opened is in the way.
	/// </summary>
	private bool CanAddWallet()
	{
		try
		{
			UiContext.Services.WalletManager.AssertWalletCanBeAdded();
			return true;
		}
		catch (InvalidOperationException ex)
		{
			Navigate().To().ShowErrorDialog(ex.Message, "Add Wallet", "A wallet cannot be added.", NavigationTarget.CompactDialogScreen);
			return false;
		}
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// The pages of the steps report to the progress. It is looked at while this screen shows, also when it comes back from one of them.
		_progress.WhenAnyValue(x => x.CurrentStep)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(current => CurrentStep = current)
			.DisposeWith(disposables);

		CurrentStep = _progress.CurrentStep;
	}
}
