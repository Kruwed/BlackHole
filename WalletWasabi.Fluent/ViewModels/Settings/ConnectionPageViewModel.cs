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
/// "Verify Connection" finds out whether what the page names answers, and changes nothing. "Done" does the same
/// and, if it answers, makes the application work with it from then on, without a restart, saves it, and closes the
/// page. If it does not answer, the page stays open and tells why.
/// </summary>
public abstract partial class ConnectionPageViewModel : RoutableViewModel, ISettingsPage
{
	/// <summary>What the button says that connects the application and closes the page.</summary>
	public const string DoneLabel = "Done";

	private readonly Connection _connection;

	/// <summary>What was answered the last time, or what the page is waiting for.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _statusMessage;

	/// <summary>Whether the last answer was a good one. The words have the colour of it.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isStatusGood;

	/// <summary>Whether the application was connected to what the page names. The page closes then.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConnected;

	/// <summary>Whether an answer is being waited for. Nothing else can be asked meanwhile, and what the page names cannot be changed.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isAsking;

	/// <summary>Gives up the answer that is being waited for when the page is left.</summary>
	private CancellationTokenSource? _leaveCts;

	/// <summary>Whether the page is the one that shows. A page that was left has nothing to close.</summary>
	private bool _isShown;

	/// <summary>Whether there is something that the page connects the application to. See <see cref="SetUpCommands"/>.</summary>
	private bool _hasSomethingToConnectTo = true;

	protected ConnectionPageViewModel(UiContext uiContext, Connection connection) : base(uiContext)
	{
		_connection = connection;

		Settings = uiContext.ApplicationSettings;
		RestartCommand = ReactiveCommand.Create(() => AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true));

		EnableBack = true;
	}

	/// <summary>What the button says that connects.</summary>
	public string NextLabel => DoneLabel;

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
	/// Tells whether "Done" can be pressed: not while an answer is being waited for, and not while what the page names
	/// cannot be asked, an address that is none for example, unless the application is connected already.
	/// </summary>
	/// <param name="hasSomethingToConnectTo">False on a page with nothing to connect to: "Done" only closes it.</param>
	public static bool CanBeDone(bool canAsk, bool isAsking, bool isConnected, bool hasSomethingToConnectTo) =>
		!isAsking && (canAsk || isConnected || !hasSomethingToConnectTo);

	/// <summary>Tells whether a press of "Done" connects the application first: it does unless that was done, or there is nothing to connect to.</summary>
	public static bool ConnectsFirst(bool isConnected, bool hasSomethingToConnectTo) =>
		hasSomethingToConnectTo && !isConnected;

	/// <summary>
	/// Tells whether the page closes after a press of "Done": once the application is connected. What did not answer
	/// keeps the page open, and a page that was left meanwhile is closed already.
	/// </summary>
	public static bool ClosesWhenDone(bool isConnected, bool hasSomethingToConnectTo, bool isShown) =>
		isShown && (isConnected || !hasSomethingToConnectTo);

	/// <summary>
	/// Sets up the two buttons. A page calls it once it knows when there is something to ask.
	/// </summary>
	/// <param name="canAsk">Whether what the page names can be asked: an address has to be one, for example.</param>
	/// <param name="hasSomethingToConnectTo">
	/// False if the application does not use what the page names, Tor that is turned off for example. There is nothing
	/// to connect to then, and "Done" closes the page.
	/// </param>
	protected void SetUpCommands(IObservable<bool> canAsk, bool hasSomethingToConnectTo = true)
	{
		_hasSomethingToConnectTo = hasSomethingToConnectTo;

		var isAsking = this.WhenAnyValue(x => x.IsAsking);
		var isConnected = this.WhenAnyValue(x => x.IsConnected);

		VerifyCommand = ReactiveCommand.CreateFromTask(
			() => AskAsync(VerifyAsync, isConnecting: false),
			canAsk.CombineLatest(isAsking, (can, asking) => can && !asking));

		// The button connects, and closes the page once the application is connected.
		NextCommand = ReactiveCommand.CreateFromTask(
			OnNextAsync,
			canAsk.CombineLatest(isAsking, isConnected, (can, asking, connected) => CanBeDone(can, asking, connected, hasSomethingToConnectTo)));
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

	/// <summary>
	/// Done: the application is connected to what the page names, and then the page closes. What does not answer keeps
	/// the page open, with the reason in sight.
	/// </summary>
	private async Task OnNextAsync()
	{
		if (ConnectsFirst(IsConnected, _hasSomethingToConnectTo))
		{
			await AskAsync(ConnectAsync, isConnecting: true);
		}

		if (ClosesWhenDone(IsConnected, _hasSomethingToConnectTo, _isShown))
		{
			Navigate().Back();
		}
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
		_isShown = true;

		disposables.Add(Disposable.Create(() =>
		{
			_isShown = false;
			leaveCts.Cancel();
			leaveCts.Dispose();
		}));
	}
}
