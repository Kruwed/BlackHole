using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>
/// Where the user tells which Bitcoin node the application works with. The node is asked right away, and if it answers
/// the application works with it from then on, without a restart.
/// </summary>
[NavigationMetaData(Title = "Connect to a Full Node")]
public partial class ConnectToFullNodeViewModel : RoutableViewModel
{
	private const string ConnectLabel = "Connect";
	private const string DoneLabel = "Done";

	[AutoNotify] private string _bitcoinRpcUri;
	[AutoNotify] private string _bitcoinRpcCredentialString;
	[AutoNotify] private string? _statusMessage;
	[AutoNotify] private bool _isConnected;
	[AutoNotify] private string _nextLabel = ConnectLabel;

	/// <summary>Gives up the attempt that is on its way when the page is left.</summary>
	private CancellationTokenSource? _leaveCts;

	public ConnectToFullNodeViewModel(UiContext uiContext) : base(uiContext)
	{
		var settings = uiContext.ApplicationSettings;

		DefaultRpcUri = RpcSettings.GetDefaultUri(settings.Network);
		IsReadOnly = settings.IsBitcoinRpcOverridden;

		// A node that was given at the start is not in the saved settings. It is shown, because it is the one in use.
		var (rpcUri, rpcCredentialString) = IsReadOnly
			? (uiContext.Services.Config.BitcoinRpcUri, uiContext.Services.Config.BitcoinRpcCredentialString)
			: (settings.BitcoinRpcUri, settings.BitcoinRpcCredentialString);

		_bitcoinRpcUri = string.IsNullOrWhiteSpace(rpcUri) ? DefaultRpcUri : rpcUri;
		_bitcoinRpcCredentialString = rpcCredentialString;

		EnableBack = true;

		this.ValidateProperty(x => x.BitcoinRpcUri, ValidateBitcoinRpcUri);
		this.ValidateProperty(x => x.BitcoinRpcCredentialString, ValidateBitcoinRpcCredentialString);

		// What a node answered to the previous settings says nothing about the new ones.
		this.WhenAnyValue(x => x.BitcoinRpcUri, x => x.BitcoinRpcCredentialString)
			.Skip(1)
			.Subscribe(_ =>
			{
				IsConnected = false;
				StatusMessage = null;
			});

		this.WhenAnyValue(x => x.IsConnected)
			.Subscribe(isConnected => NextLabel = isConnected ? DoneLabel : ConnectLabel);

		var canGoOn = this.WhenAnyValue(
			x => x.BitcoinRpcUri,
			x => x.BitcoinRpcCredentialString,
			x => x.IsConnected,
			(uri, credentials, isConnected) =>
				isConnected || (!IsReadOnly && RpcSettings.TryParseUri(uri, out _) && RpcSettings.TryParseCredentials(credentials, out _)));

		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync, canGoOn);
	}

	/// <summary>
	/// A node that was given on the command line or in the environment wins over the saved one, so saving another one would have no effect.
	/// </summary>
	public bool IsReadOnly { get; }

	/// <summary>Where a Bitcoin Core on this computer listens on the network of the application. It serves as the example.</summary>
	public string DefaultRpcUri { get; }

	private void ValidateBitcoinRpcUri(IValidationErrors errors)
	{
		if (!string.IsNullOrWhiteSpace(BitcoinRpcUri) && !RpcSettings.TryParseUri(BitcoinRpcUri, out _))
		{
			errors.Add(ErrorSeverity.Error, $"The address must look like {DefaultRpcUri}.");
		}
	}

	private void ValidateBitcoinRpcCredentialString(IValidationErrors errors)
	{
		if (!RpcSettings.TryParseCredentials(BitcoinRpcCredentialString, out _))
		{
			errors.Add(ErrorSeverity.Error, "The credentials must be user:password or cookiefile=path.");
		}
	}

	private async Task OnNextAsync()
	{
		if (IsConnected)
		{
			Navigate().Back();
			return;
		}

		if (!RpcSettings.TryParseUri(BitcoinRpcUri, out var rpcUri))
		{
			return;
		}

		var credentialString = BitcoinRpcCredentialString.Trim();
		var cancellationToken = _leaveCts?.Token ?? CancellationToken.None;

		IsBusy = true;

		try
		{
			var result = await UiContext.Services.ConnectToBitcoinNodeAsync(rpcUri.ToString(), credentialString, cancellationToken);

			if (result.IsOk)
			{
				UiContext.ApplicationSettings.SetConnectedBitcoinNode(rpcUri, credentialString);

				IsConnected = true;
				StatusMessage = $"Connected. {Constants.AppName} works with this node from now on.";
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

		// The notification that the node cannot be reached sends the user to the settings of the node. Here they are
		// taking care of the node already, and in a small window the notification lies on the button of this page.
		UiContext.MainViewModel?.DismissBitcoinRpcError();

		var leaveCts = new CancellationTokenSource();
		_leaveCts = leaveCts;

		disposables.Add(Disposable.Create(() =>
		{
			leaveCts.Cancel();
			leaveCts.Dispose();
		}));
	}
}
