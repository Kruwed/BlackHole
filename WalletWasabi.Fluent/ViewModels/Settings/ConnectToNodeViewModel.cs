using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// Where the user tells which Bitcoin node the application works with, and on which network it is. The wallet gets
/// everything from that node. It comes second on the welcome.
/// </summary>
/// <remarks>The network is a setting that takes a restart. The node is asked on the network that the application runs on.</remarks>
[NavigationMetaData(Title = "Connect to Node")]
public partial class ConnectToNodeViewModel : ConnectionPageViewModel
{
	[AutoNotify] private string _bitcoinRpcUri;
	[AutoNotify] private string _bitcoinRpcCredentialString;

	public ConnectToNodeViewModel(UiContext uiContext) : base(uiContext, Connection.Node)
	{
		DefaultRpcUri = RpcSettings.GetDefaultUri(uiContext.Services.GetNetwork());
		IsReadOnly = Settings.IsBitcoinRpcOverridden;

		// A node that was given at the start is not in the saved settings. It is shown, because it is the one in use.
		var (rpcUri, rpcCredentialString) = IsReadOnly
			? (uiContext.Services.Config.BitcoinRpcUri, uiContext.Services.Config.BitcoinRpcCredentialString)
			: (Settings.BitcoinRpcUri, Settings.BitcoinRpcCredentialString);

		_bitcoinRpcUri = string.IsNullOrWhiteSpace(rpcUri) ? DefaultRpcUri : rpcUri;
		_bitcoinRpcCredentialString = rpcCredentialString;

		this.ValidateProperty(x => x.BitcoinRpcUri, ValidateBitcoinRpcUri);
		this.ValidateProperty(x => x.BitcoinRpcCredentialString, ValidateBitcoinRpcCredentialString);

		// What a node answered to the previous settings says nothing about the new ones.
		this.WhenAnyValue(x => x.BitcoinRpcUri, x => x.BitcoinRpcCredentialString)
			.Skip(1)
			.Subscribe(_ => ForgetAnswer());

		// A node that was given at the start cannot be changed here, but it is asked like any other.
		SetUpCommands(
			canAsk: this.WhenAnyValue(
				x => x.BitcoinRpcUri,
				x => x.BitcoinRpcCredentialString,
				(uri, credentials) => RpcSettings.TryParseUri(uri, out _) && RpcSettings.TryParseCredentials(credentials, out _)));
	}

	/// <summary>
	/// A node that was given on the command line or in the environment wins over the saved one, so saving another one would have no effect.
	/// </summary>
	public bool IsReadOnly { get; }

	/// <summary>Where a Bitcoin Core on this computer listens on the network of the application. It serves as the example.</summary>
	public string DefaultRpcUri { get; }

	public IEnumerable<Network> Networks { get; } = PersistentConfigManager.Networks;

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

	protected override string GetWaitingMessage(bool isConnecting) =>
		isConnecting ? "Connecting to the node…" : "Asking the node…";

	protected override async Task<ConnectionAnswer> VerifyAsync(CancellationToken cancellationToken)
	{
		if (!RpcSettings.TryParseUri(BitcoinRpcUri, out var rpcUri))
		{
			return new ConnectionAnswer(false, $"The address must look like {DefaultRpcUri}.");
		}

		var result = await UiContext.Services.VerifyBitcoinNodeAsync(rpcUri.ToString(), BitcoinRpcCredentialString.Trim(), cancellationToken);

		return result.IsOk
			? new ConnectionAnswer(true, "The node answers.")
			: new ConnectionAnswer(false, result.Error);
	}

	protected override async Task<ConnectionAnswer> ConnectAsync(CancellationToken cancellationToken)
	{
		if (!RpcSettings.TryParseUri(BitcoinRpcUri, out var rpcUri))
		{
			return new ConnectionAnswer(false, $"The address must look like {DefaultRpcUri}.");
		}

		var credentialString = BitcoinRpcCredentialString.Trim();
		var result = await UiContext.Services.ConnectToBitcoinNodeAsync(rpcUri.ToString(), credentialString, cancellationToken);

		if (!result.IsOk)
		{
			return new ConnectionAnswer(false, result.Error);
		}

		if (!IsReadOnly)
		{
			Settings.SetConnectedBitcoinNode(rpcUri, credentialString);
		}

		return new ConnectionAnswer(true, $"Connected. {Constants.AppName} works with this node from now on.");
	}
}
