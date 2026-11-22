using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// Where the user sees to it that the application is connected to Tor, and chooses how Tor is used. It comes first
/// on the welcome, as the relays and the coordinator are reached through Tor.
/// </summary>
/// <remarks>
/// The application starts Tor when it starts, so connecting waits for that Tor and starts nothing. How Tor is used
/// is a setting that takes a restart.
/// </remarks>
[NavigationMetaData(Title = "Connect to Tor")]
public partial class ConnectToTorViewModel : ConnectionPageViewModel
{
	public ConnectToTorViewModel(UiContext uiContext) : base(uiContext, Connection.Tor)
	{
		var mode = uiContext.Services.GetUseTor();
		var isRegTest = uiContext.Services.GetNetwork() == Network.RegTest;

		IsTorUsed = mode != TorMode.Disabled;
		IsTorASetting = !isRegTest;
		Description = DescribeUse(mode, isRegTest);

		// A Tor that is not used cannot be asked.
		SetUpCommands(canAsk: Observable.Return(IsTorUsed));
	}

	/// <summary>Whether the application uses Tor. If it does not, there is nothing to connect to.</summary>
	public bool IsTorUsed { get; }

	/// <summary>Whether the settings decide how Tor is used. On regtest they do not: Tor is not used there.</summary>
	public bool IsTorASetting { get; }

	/// <summary>What Tor is for, and how the application gets to one.</summary>
	public string Description { get; }

	public IReadOnlyList<TorMode> TorModes => Enum.GetValues<TorMode>();

	/// <summary>Tells what Tor is used for and where it comes from, or that it is not used.</summary>
	/// <param name="mode">How the application uses Tor since it started.</param>
	/// <param name="isRegTest">On regtest the application does not use Tor, whatever the setting says.</param>
	public static string DescribeUse(TorMode mode, bool isRegTest) =>
		mode switch
		{
			TorMode.Enabled => $"{Constants.AppName} reaches the Nostr relays and the coordinator through Tor, and your node too if it is an onion service. It starts a Tor of its own for that.",
			TorMode.EnabledOnlyRunning => $"{Constants.AppName} reaches the Nostr relays and the coordinator through Tor, and your node too if it is an onion service. It uses the Tor that runs on this computer and starts none of its own.",
			_ when isRegTest => $"Tor is not used on regtest: {Constants.AppName} reaches your node, the Nostr relays and the coordinator directly.",
			_ => $"Tor is turned off: {Constants.AppName} reaches your node, the Nostr relays and the coordinator directly, and they see the address of this computer."
		};

	protected override string GetWaitingMessage(bool isConnecting) =>
		isConnecting ? "Connecting to Tor…" : "Asking Tor…";

	protected override async Task<ConnectionAnswer> VerifyAsync(CancellationToken cancellationToken)
	{
		var result = await UiContext.Services.VerifyTorAsync(cancellationToken);

		return result.IsOk
			? new ConnectionAnswer(true, "Tor is connected.")
			: new ConnectionAnswer(false, result.Error);
	}

	protected override async Task<ConnectionAnswer> ConnectAsync(CancellationToken cancellationToken)
	{
		var result = await UiContext.Services.ConnectToTorAsync(cancellationToken);

		return result.IsOk
			? new ConnectionAnswer(true, "Connected to Tor.")
			: new ConnectionAnswer(false, result.Error);
	}
}
