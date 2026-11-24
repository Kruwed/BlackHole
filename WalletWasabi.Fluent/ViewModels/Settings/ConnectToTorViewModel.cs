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

		// A Tor that is not used cannot be asked, and there is nothing to connect to: "Done" closes the page.
		SetUpCommands(canAsk: Observable.Return(IsTorUsed), hasSomethingToConnectTo: IsTorUsed);
	}

	/// <summary>Whether the application uses Tor. If it does not, there is nothing to connect to.</summary>
	public bool IsTorUsed { get; }

	/// <summary>Whether the settings decide how Tor is used. On regtest they do not: Tor is not used there.</summary>
	public bool IsTorASetting { get; }

	/// <summary>What the page says about Tor while Tor is not used. Null while it is used: there is nothing to say then.</summary>
	public string? Description { get; }

	public IReadOnlyList<TorMode> TorModes => Enum.GetValues<TorMode>();

	/// <summary>Tells that Tor is not used, and what follows from that. About a Tor that is used the page says nothing: null.</summary>
	/// <param name="mode">How the application uses Tor since it started.</param>
	/// <param name="isRegTest">On regtest the application does not use Tor, whatever the setting says.</param>
	public static string? DescribeUse(TorMode mode, bool isRegTest) =>
		mode switch
		{
			// With Tor in use the page has the choice of how, and the two buttons. That says it.
			TorMode.Enabled or TorMode.EnabledOnlyRunning => null,
			_ when isRegTest => $"Tor is not used on regtest: {Constants.AppName} reaches your node, the Nostr relays and the coordinator directly.",
			_ => $"Tor is turned off: {Constants.AppName} reaches your node, the Nostr relays and the coordinator directly, and they see the address of this computer."
		};

	protected override string GetWaitingMessage(bool isConnecting) =>
		isConnecting ? "Connecting to Tor…" : "Asking Tor…";

	protected override async Task<ConnectionAnswer> VerifyAsync(CancellationToken cancellationToken)
	{
		var result = await UiContext.Services.VerifyTorAsync(cancellationToken);

		if (!result.IsOk)
		{
			return new ConnectionAnswer(false, result.Error);
		}

		return new ConnectionAnswer(true, DescribeConnection(await UiContext.Services.CountTorRelayAddressesAsync(cancellationToken)));
	}

	/// <summary>
	/// What the page answers when Tor is connected: with how many unique Tor IP addresses. They are the addresses of the
	/// relays that the circuits of Tor go through at that moment, first hops, middle relays and exits, every address once.
	/// </summary>
	/// <param name="relayAddressCount">The number, or null if Tor could not be asked for it: a Tor that the application did not start tells nothing about its circuits.</param>
	public static string DescribeConnection(int? relayAddressCount) =>
		relayAddressCount switch
		{
			null => "Tor is connected.",
			0 => "Tor is connected with 0 unique Tor IP addresses: it has no circuit open at the moment.",
			1 => "Tor is connected with 1 unique Tor IP address.",
			var count => $"Tor is connected with {count} unique Tor IP addresses."
		};

	protected override async Task<ConnectionAnswer> ConnectAsync(CancellationToken cancellationToken)
	{
		var result = await UiContext.Services.ConnectToTorAsync(cancellationToken);

		return result.IsOk
			? new ConnectionAnswer(true, "Connected to Tor.")
			: new ConnectionAnswer(false, result.Error);
	}
}
