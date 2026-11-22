using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>
/// Where the user sees to it that the application is connected to Tor. It is the first step of the welcome, as the
/// relays and the coordinator of the steps after it are reached through Tor.
/// </summary>
/// <remarks>
/// The application starts Tor when it starts, so the page has nothing to ask: it waits for Tor when it is opened and
/// tells how that went. How Tor is used is a setting, and a change of it takes a restart, so it is not changed here.
/// </remarks>
[NavigationMetaData(Title = "Connect to Tor")]
public partial class ConnectToTorViewModel : RoutableViewModel
{
	private const string ConnectLabel = "Connect";
	private const string DoneLabel = "Done";

	private const string ConnectingMessage = "Connecting to Tor…";

	private readonly Action _onConnected;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _statusMessage;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConnected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConnecting;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _nextLabel;

	/// <summary>Gives up the attempt that is on its way when the page is left.</summary>
	private CancellationTokenSource? _leaveCts;

	/// <param name="onConnected">Is told when the application is connected to Tor, or when the user has seen that Tor is not used.</param>
	public ConnectToTorViewModel(UiContext uiContext, Action onConnected) : base(uiContext)
	{
		_onConnected = onConnected;

		var mode = uiContext.Services.GetUseTor();
		var isRegTest = uiContext.Services.GetNetwork() == Network.RegTest;

		IsTorUsed = mode != TorMode.Disabled;
		IsTorASetting = !isRegTest;
		Description = DescribeUse(mode, isRegTest);
		_nextLabel = IsTorUsed ? ConnectLabel : DoneLabel;

		EnableBack = true;

		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync, this.WhenAnyValue(x => x.IsConnecting, isConnecting => !isConnecting));
	}

	/// <summary>Whether the application uses Tor. If it does not, there is nothing to connect to.</summary>
	public bool IsTorUsed { get; }

	/// <summary>Whether the settings decide how Tor is used. On regtest they do not: Tor is not used there.</summary>
	public bool IsTorASetting { get; }

	/// <summary>What Tor is for, and how the application gets to one.</summary>
	public string Description { get; }

	/// <summary>Tells what Tor is used for and where it comes from, or that it is not used.</summary>
	/// <param name="isRegTest">On regtest the application does not use Tor, whatever the setting says.</param>
	public static string DescribeUse(TorMode mode, bool isRegTest) =>
		mode switch
		{
			TorMode.Enabled => $"{Constants.AppName} reaches the Nostr relays and the coordinator through Tor, and your node too if it is an onion service. It starts a Tor of its own for that.",
			TorMode.EnabledOnlyRunning => $"{Constants.AppName} reaches the Nostr relays and the coordinator through Tor, and your node too if it is an onion service. It uses the Tor that runs on this computer and starts none of its own.",
			_ when isRegTest => $"Tor is not used on regtest: {Constants.AppName} reaches your node, the Nostr relays and the coordinator directly.",
			_ => $"Tor is turned off in the settings: {Constants.AppName} reaches your node, the Nostr relays and the coordinator directly, and they see the address of this computer."
		};

	private async Task OnNextAsync()
	{
		if (!IsTorUsed)
		{
			// There is nothing to connect to: the user has seen it.
			_onConnected();
			Navigate().Back();
			return;
		}

		if (IsConnected)
		{
			Navigate().Back();
			return;
		}

		await ConnectAsync();
	}

	private async Task ConnectAsync()
	{
		if (IsConnecting || IsConnected)
		{
			return;
		}

		var cancellationToken = _leaveCts?.Token ?? CancellationToken.None;

		IsConnecting = true;
		StatusMessage = ConnectingMessage;

		try
		{
			var result = await UiContext.Services.ConnectToTorAsync(cancellationToken);

			if (result.IsOk)
			{
				IsConnected = true;
				NextLabel = DoneLabel;
				StatusMessage = "Connected to Tor.";
				_onConnected();
			}
			else
			{
				StatusMessage = result.Error;
			}
		}
		catch (OperationCanceledException)
		{
			// The page was left: nobody is waiting for the answer.
			StatusMessage = null;
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			StatusMessage = ex.ToUserFriendlyString();
		}
		finally
		{
			IsConnecting = false;
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

		// Tor is being started already, so there is nothing to wait for with the attempt.
		if (IsTorUsed)
		{
			_ = ConnectAsync();
		}
	}
}
