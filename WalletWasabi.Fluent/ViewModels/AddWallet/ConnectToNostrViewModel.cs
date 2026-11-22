using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Settings;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>
/// Where the user names the Nostr relays that the application connects to. The relays are tried right away, and if one
/// of them answers the application posts to them from then on, without a restart. It is the third step of the welcome.
/// </summary>
[NavigationMetaData(Title = "Connect to Nostr")]
public partial class ConnectToNostrViewModel : RoutableViewModel
{
	private const string ConnectLabel = "Connect";
	private const string DoneLabel = "Done";

	private readonly Action _onConnected;

	[AutoNotify] private string _relays;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _statusMessage;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConnected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _nextLabel = ConnectLabel;

	/// <summary>Gives up the attempt that is on its way when the page is left.</summary>
	private CancellationTokenSource? _leaveCts;

	/// <param name="onConnected">Is told when the application posts to the relays that the user named.</param>
	public ConnectToNostrViewModel(UiContext uiContext, Action onConnected) : base(uiContext)
	{
		_onConnected = onConnected;

		var settings = uiContext.ApplicationSettings;

		IsReadOnly = settings.IsNostrRelaysOverridden;

		// Relays that were given at the start are not in the saved settings. They are shown, because they are the ones in use.
		_relays = NostrSettingsTabViewModel.FormatRelays(IsReadOnly ? uiContext.Services.Config.NostrRelays : settings.NostrRelays);

		EnableBack = true;

		this.ValidateProperty(x => x.Relays, ValidateRelays);

		// What the relays answered before says nothing about another list.
		this.WhenAnyValue(x => x.Relays)
			.Skip(1)
			.Subscribe(_ =>
			{
				IsConnected = false;
				StatusMessage = null;
			});

		this.WhenAnyValue(x => x.IsConnected)
			.Subscribe(isConnected => NextLabel = isConnected ? DoneLabel : ConnectLabel);

		var canGoOn = this.WhenAnyValue(
			x => x.Relays,
			x => x.IsConnected,
			(relays, isConnected) => isConnected || TryReadRelays(relays, out _, out _));

		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync, canGoOn);
	}

	/// <summary>
	/// Relays that were given on the command line or in the environment win over the saved ones, so saving others would have no effect.
	/// </summary>
	public bool IsReadOnly { get; }

	/// <summary>
	/// Reads the relays that the user named, one on a line. Here there has to be one at least: with none there is
	/// nothing to connect to.
	/// </summary>
	/// <param name="problem">What is wrong with the list, if it cannot be used.</param>
	public static bool TryReadRelays(string text, out string[] relays, out string? problem)
	{
		try
		{
			relays = NostrSettingsTabViewModel.ParseRelays(text);
		}
		catch (FormatException ex)
		{
			relays = [];
			problem = ex.Message;
			return false;
		}

		problem = relays.Length == 0 ? "Name one relay at least, like wss://relay.example." : null;
		return problem is null;
	}

	/// <summary>
	/// Tells how connecting to the relays went. The application is connected if one of them answered: it then posts to
	/// all of them, and a relay that did not answer this time gets every offer all the same.
	/// </summary>
	public static (bool IsConnected, string Message) DescribeConnection(IReadOnlyList<RelayReachability> relays)
	{
		var reached = relays.Count(relay => relay.IsReachable);
		var problems = relays.Where(relay => !relay.IsReachable).Select(relay => $"{relay.Relay}: {relay.Problem}");

		var summary = (reached, relays.Count) switch
		{
			(0, 1) => "The relay cannot be reached.",
			(0, _) => $"None of the {relays.Count} relays can be reached.",
			(1, 1) => $"Connected. {Constants.AppName} posts to this relay from now on.",
			_ when reached == relays.Count => $"Connected to all {relays.Count} relays. {Constants.AppName} posts to them from now on.",
			_ => $"Connected to {reached} of {relays.Count} relays. {Constants.AppName} posts to all of them from now on."
		};

		return (reached > 0, string.Join(Environment.NewLine, problems.Prepend(summary)));
	}

	private void ValidateRelays(IValidationErrors errors)
	{
		if (!TryReadRelays(Relays, out _, out var problem) && problem is not null)
		{
			errors.Add(ErrorSeverity.Error, problem);
		}
	}

	private async Task OnNextAsync()
	{
		if (IsConnected)
		{
			Navigate().Back();
			return;
		}

		if (!TryReadRelays(Relays, out var relays, out _))
		{
			return;
		}

		var cancellationToken = _leaveCts?.Token ?? CancellationToken.None;

		IsBusy = true;

		try
		{
			var answers = await UiContext.Services.ConnectToNostrRelaysAsync(NostrRelayPublisher.ParseRelays(relays), cancellationToken);
			var (isConnected, message) = DescribeConnection(answers);

			if (isConnected)
			{
				if (!IsReadOnly)
				{
					UiContext.ApplicationSettings.SetConnectedNostrRelays(relays);
				}

				IsConnected = true;
				_onConnected();
			}

			StatusMessage = message;
		}
		catch (OperationCanceledException)
		{
			// The page was left: nobody is waiting for the answer.
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			StatusMessage = ex.ToUserFriendlyString();
		}
		finally
		{
			IsBusy = false;
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
