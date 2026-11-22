using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>A page of the settings. It tells when what was changed takes a restart, and offers the restart.</summary>
public interface ISettingsPage
{
	ApplicationSettings Settings { get; }

	ICommand RestartCommand { get; }
}

/// <summary>What something answered when a page asked it, in words for the user.</summary>
/// <param name="IsGood">Whether the application can work with it.</param>
public record ConnectionAnswer(bool IsGood, string Message);

/// <summary>
/// A page of the settings where the user connects the application to something: to Tor, to the node, to the Nostr
/// relays or to the coordinator. It is the same page on the welcome and in the settings, and it has two buttons.
/// "Verify Connection" finds out whether what the page names answers, and changes nothing. "Connect" does the same
/// and, if it answers, makes the application work with it from then on, without a restart, and saves it.
/// </summary>
public abstract partial class ConnectionPageViewModel : RoutableViewModel, ISettingsPage
{
	public const string ConnectLabel = "Connect";
	public const string DoneLabel = "Done";

	private readonly Connection _connection;

	/// <summary>What was answered the last time, or what the page is waiting for.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _statusMessage;

	/// <summary>Whether the last answer was a good one. The words have the colour of it.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isStatusGood;

	/// <summary>Whether the application was connected to what the page names. The button closes the page then.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConnected;

	/// <summary>Whether an answer is being waited for. Nothing else can be asked meanwhile, and what the page names cannot be changed.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isAsking;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _nextLabel = ConnectLabel;

	/// <summary>Gives up the answer that is being waited for when the page is left.</summary>
	private CancellationTokenSource? _leaveCts;

	protected ConnectionPageViewModel(UiContext uiContext, Connection connection) : base(uiContext)
	{
		_connection = connection;

		Settings = uiContext.ApplicationSettings;
		RestartCommand = ReactiveCommand.Create(() => AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true));

		EnableBack = true;

		this.WhenAnyValue(x => x.IsConnected)
			.Subscribe(isConnected => NextLabel = isConnected ? DoneLabel : ConnectLabel);
	}

	public ApplicationSettings Settings { get; }

	/// <summary>
	/// Whether the settings that take a restart cannot be changed, because something was given on the command line or
	/// in the environment when the application was started. Saving them would have no effect on what was given.
	/// </summary>
	public bool AreSettingsReadOnly => Settings.IsOverridden;

	public ICommand RestartCommand { get; }

	/// <summary>Finds out whether what the page names answers. Nothing changes.</summary>
	public ICommand? VerifyCommand { get; private set; }

	/// <summary>
	/// Sets up the two buttons. A page calls it once it knows when there is something to ask.
	/// </summary>
	/// <param name="canAsk">Whether what the page names can be asked: an address has to be one, for example.</param>
	protected void SetUpCommands(IObservable<bool> canAsk)
	{
		var isAsking = this.WhenAnyValue(x => x.IsAsking);
		var isConnected = this.WhenAnyValue(x => x.IsConnected);

		VerifyCommand = ReactiveCommand.CreateFromTask(
			() => AskAsync(VerifyAsync, isConnecting: false),
			canAsk.CombineLatest(isAsking, (can, asking) => can && !asking));

		// Once the application is connected, the button closes the page.
		NextCommand = ReactiveCommand.CreateFromTask(
			OnNextAsync,
			canAsk.CombineLatest(isAsking, isConnected, (can, asking, connected) => !asking && (can || connected)));
	}

	/// <summary>Asks what the page names whether it answers.</summary>
	protected abstract Task<ConnectionAnswer> VerifyAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Asks what the page names and, if it answers, makes the application work with it and saves it.
	/// </summary>
	protected abstract Task<ConnectionAnswer> ConnectAsync(CancellationToken cancellationToken);

	/// <summary>
	/// What the page tells while it waits for an answer. It stays in sight meanwhile, so that the user can go on
	/// typing where they were once the answer is there.
	/// </summary>
	protected abstract string GetWaitingMessage(bool isConnecting);

	/// <summary>Forgets what was answered. A page does that when the user changes what it names: the answer was about something else.</summary>
	protected void ForgetAnswer()
	{
		IsConnected = false;
		StatusMessage = null;
	}

	private async Task OnNextAsync()
	{
		if (IsConnected)
		{
			Navigate().Back();
			return;
		}

		await AskAsync(ConnectAsync, isConnecting: true);
	}

	private async Task AskAsync(Func<CancellationToken, Task<ConnectionAnswer>> ask, bool isConnecting)
	{
		if (IsAsking)
		{
			return;
		}

		var cancellationToken = _leaveCts?.Token ?? CancellationToken.None;

		IsAsking = true;
		IsStatusGood = false;
		StatusMessage = GetWaitingMessage(isConnecting);

		try
		{
			var answer = await ask(cancellationToken);

			if (isConnecting && answer.IsGood)
			{
				IsConnected = true;
				UiContext.Connections.SetConnected(_connection);
			}

			IsStatusGood = answer.IsGood;
			StatusMessage = answer.Message;
		}
		catch (OperationCanceledException)
		{
			// The page was left: nobody is waiting for the answer.
			StatusMessage = null;
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			IsStatusGood = false;
			StatusMessage = ex.ToUserFriendlyString();
		}
		finally
		{
			IsAsking = false;
		}
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// The page is left with the back arrow: there is nothing to cancel.
		SetupCancel(enableCancel: false, enableCancelOnEscape: false, enableCancelOnPressed: false);

		var leaveCts = new CancellationTokenSource();
		_leaveCts = leaveCts;

		disposables.Add(Disposable.Create(() =>
		{
			leaveCts.Cancel();
			leaveCts.Dispose();
		}));
	}
}
