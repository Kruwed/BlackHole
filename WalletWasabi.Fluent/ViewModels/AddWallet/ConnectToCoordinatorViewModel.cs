using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>
/// Where the user tells which coordinator the application coinjoins with. The coordinator is asked right away, and if
/// it answers the application works with it from then on, without a restart. It is the fourth step of the welcome.
/// </summary>
[NavigationMetaData(Title = "Connect to Coordinator")]
public partial class ConnectToCoordinatorViewModel : RoutableViewModel
{
	private const string ConnectLabel = "Connect";
	private const string DoneLabel = "Done";

	private readonly Action _onConnected;

	[AutoNotify] private string _coordinatorUri;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _statusMessage;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConnected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _nextLabel = ConnectLabel;

	/// <summary>Gives up the attempt that is on its way when the page is left.</summary>
	private CancellationTokenSource? _leaveCts;

	/// <param name="onConnected">Is told when the application coinjoins with the coordinator that the user named.</param>
	public ConnectToCoordinatorViewModel(UiContext uiContext, Action onConnected) : base(uiContext)
	{
		_onConnected = onConnected;

		var settings = uiContext.ApplicationSettings;

		ExampleUri = GetExampleUri(settings.Network);
		IsReadOnly = settings.IsCoordinatorUriOverridden;

		// A coordinator that was given at the start is not in the saved settings. It is shown, because it is the one in use.
		_coordinatorUri = IsReadOnly ? uiContext.Services.Config.CoordinatorUri : settings.CoordinatorUri;

		EnableBack = true;

		this.ValidateProperty(x => x.CoordinatorUri, ValidateCoordinatorUri);

		// What a coordinator answered says nothing about another address.
		this.WhenAnyValue(x => x.CoordinatorUri)
			.Skip(1)
			.Subscribe(_ =>
			{
				IsConnected = false;
				StatusMessage = null;
			});

		this.WhenAnyValue(x => x.IsConnected)
			.Subscribe(isConnected => NextLabel = isConnected ? DoneLabel : ConnectLabel);

		var canGoOn = this.WhenAnyValue(
			x => x.CoordinatorUri,
			x => x.IsConnected,
			(uri, isConnected) => isConnected || CoordinatorConnectionCheck.TryParseUri(uri, out _));

		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync, canGoOn);
	}

	/// <summary>
	/// A coordinator that was given on the command line or in the environment wins over the saved one, so saving another one would have no effect.
	/// </summary>
	public bool IsReadOnly { get; }

	/// <summary>What the address of a coordinator looks like.</summary>
	public string ExampleUri { get; }

	/// <summary>
	/// Gives an address that shows what the address of a coordinator looks like. On regtest it is where the coordinator
	/// of this repository listens when it runs on this computer; elsewhere there is no coordinator to point at.
	/// </summary>
	public static string GetExampleUri(Network network) =>
		network == Network.RegTest ? Constants.RegTestCoordinatorUri : "https://coordinator.example/";

	private void ValidateCoordinatorUri(IValidationErrors errors)
	{
		if (!string.IsNullOrWhiteSpace(CoordinatorUri) && !CoordinatorConnectionCheck.TryParseUri(CoordinatorUri, out _))
		{
			errors.Add(ErrorSeverity.Error, $"The address must look like {ExampleUri}.");
		}
	}

	private async Task OnNextAsync()
	{
		if (IsConnected)
		{
			Navigate().Back();
			return;
		}

		if (!CoordinatorConnectionCheck.TryParseUri(CoordinatorUri, out var coordinatorUri))
		{
			return;
		}

		var cancellationToken = _leaveCts?.Token ?? CancellationToken.None;

		IsBusy = true;

		try
		{
			var result = await UiContext.Services.ConnectToCoordinatorAsync(coordinatorUri.ToString(), cancellationToken);

			if (result.IsOk)
			{
				if (!IsReadOnly)
				{
					UiContext.ApplicationSettings.SetConnectedCoordinator(coordinatorUri);
				}

				IsConnected = true;
				StatusMessage = $"Connected. {Constants.AppName} coinjoins with this coordinator from now on.";
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
