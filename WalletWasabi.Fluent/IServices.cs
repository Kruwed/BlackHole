using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Discoverability;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Helpers;
using WalletWasabi.Services;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent;

public interface IServices
{
	string DataDir { get; }
	string PersistentConfigFilePath { get; }
	PersistentConfig PersistentConfig { get; }
	WalletManager WalletManager { get; }
	UiConfig UiConfig { get; }
	Config Config { get; }
	EventBus EventBus { get; }

	/// <summary>Height of the chain, as the user's node tells it. Zero as long as the node did not answer.</summary>
	uint GetNodeTipHeight();

	/// <summary>What the user's node told about itself the last time, if it answered.</summary>
	ConnectedRpcStatus? GetNodeStatus();

	/// <summary>Whether the user's node answered the last time the application asked it about itself. Null as long as it was not asked.</summary>
	bool? IsNodeAnswering();

	/// <summary>The fee rates that the user's node estimated the last time, if it gave any since the application started.</summary>
	FeeRateEstimations? GetFeeRateEstimations();

	/// <summary>
	/// Makes the application work with the node at the given RPC interface from now on, without a restart, provided that
	/// the node answers and is on the network of the application. Otherwise the application keeps the node that it has.
	/// </summary>
	/// <returns>Nothing if the application works with the node, otherwise the reason why it does not, in words for the user.</returns>
	Task<Result<string>> ConnectToBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken);

	/// <summary>
	/// Asks the node at the given RPC interface whether the application could work with it, and leaves it at that: the
	/// application keeps the node that it has.
	/// </summary>
	/// <returns>Nothing if the application could work with the node, otherwise the reason why it could not, in words for the user.</returns>
	Task<Result<string>> VerifyBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken);

	/// <summary>
	/// Sees to it that the application is connected to Tor: Tor has to run and to have connected to the Tor network.
	/// The application starts Tor when it starts, so this waits for it.
	/// </summary>
	/// <returns>Nothing if the application is connected to Tor, otherwise the reason why it is not, in words for the user.</returns>
	Task<Result<string>> ConnectToTorAsync(CancellationToken cancellationToken);

	/// <summary>Tells whether the application is connected to Tor right now, without waiting for a Tor that is still starting.</summary>
	/// <returns>Nothing if the application is connected to Tor, otherwise the reason why it is not, in words for the user.</returns>
	Task<Result<string>> VerifyTorAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Tells how many different IP addresses the Tor relays have that the circuits of the application's Tor go through
	/// right now: first hops, middle relays and exits.
	/// </summary>
	/// <returns>The number, or null if Tor cannot be asked for it.</returns>
	Task<int?> CountTorRelayAddressesAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Makes the application post to the given Nostr relays from now on, without a restart, provided that at least one
	/// of them can be reached. Otherwise the application keeps the relays that it has. Nothing is sent to a relay.
	/// </summary>
	/// <returns>Whether each of the relays could be reached.</returns>
	Task<IReadOnlyList<RelayReachability>> ConnectToNostrRelaysAsync(IReadOnlyList<Uri> relays, CancellationToken cancellationToken);

	/// <summary>
	/// Finds out whether the given Nostr relays can be reached, and leaves it at that: the application keeps the
	/// relays that it has. Nothing is sent to a relay.
	/// </summary>
	/// <returns>Whether each of the relays could be reached.</returns>
	Task<IReadOnlyList<RelayReachability>> VerifyNostrRelaysAsync(IReadOnlyList<Uri> relays, CancellationToken cancellationToken);

	/// <summary>The Nostr relays that the offers of the wallet are posted to now.</summary>
	IReadOnlyList<Uri> GetNostrRelays();

	/// <summary>How it stands with the Nostr relay that the application runs itself: whether it runs, and at which onion address.</summary>
	WalletWasabi.NostrRelay.NostrRelayState GetNostrRelayState();

	/// <summary>What the relay runs with at the moment. Null if none runs.</summary>
	WalletWasabi.NostrRelay.NostrRelaySettings? GetNostrRelaySettings();

	/// <summary>Why the application cannot run a relay as it was started: a relay takes a Tor that the application started. Null if it can.</summary>
	string? GetNostrRelayObstacle();

	/// <summary>
	/// Sees to it that the application runs its Nostr relay with the given choice of events, without a restart, and
	/// waits for the onion address of the relay to be published.
	/// </summary>
	/// <returns>How it stands with the relay: it runs, or it does not yet, or it cannot, and then why.</returns>
	Task<WalletWasabi.NostrRelay.NostrRelayState> RunNostrRelayAsync(WalletWasabi.NostrRelay.NostrRelaySettings settings, CancellationToken cancellationToken);

	/// <summary>Stops the Nostr relay of the application.</summary>
	Task StopNostrRelayAsync();

	/// <summary>Asks the relay of the application at its onion address, through Tor, the way anybody else reaches it. Nothing is sent to it.</summary>
	/// <returns>Nothing if the relay answers, otherwise the reason why it does not, in words for the user.</returns>
	Task<Result<string>> VerifyNostrRelayAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Makes the application coinjoin with the coordinator at the given address from now on, without a restart, provided
	/// that the coordinator answers. Otherwise the application keeps the coordinator that it has, or none.
	/// </summary>
	/// <returns>Nothing if the application works with the coordinator, otherwise the reason why it does not, in words for the user.</returns>
	Task<Result<string>> ConnectToCoordinatorAsync(string coordinatorUri, CancellationToken cancellationToken);

	/// <summary>
	/// Asks the coordinator at the given address whether it answers, and leaves it at that: the application keeps the
	/// coordinator that it has, or none.
	/// </summary>
	/// <returns>Nothing if the coordinator answers, otherwise the reason why it does not, in words for the user.</returns>
	Task<Result<string>> VerifyCoordinatorAsync(string coordinatorUri, CancellationToken cancellationToken);

	/// <summary>The coordinator that the application coinjoins with now, if there is one.</summary>
	Uri? GetCoordinatorUri();

	/// <summary>Height of the last block that the wallet processed. The confirmations of its transactions are counted to there. Zero without a wallet.</summary>
	uint GetTipHeight();

	Network GetNetwork();
	bool HasWallet();
	Task StartWalletAsync(Wallet wallet);
	void AddWallet(KeyManager keyManager);

	TorMode GetUseTor();

	double? GetWindowWidth();
	double? GetWindowHeight();
	void SetWindowWidth(double? width);
	void SetWindowHeight(double? height);
	int GetFeeTarget();
	void SetFeeTarget(int value);

	T? GetHostedService<T>() where T : class, Microsoft.Extensions.Hosting.IHostedService;

	/// <summary>The offers of the other makers, as the Nostr relays tell them. The relays are asked when the order book is asked to.</summary>
	WalletWasabi.JoinMarket.OrderBookService GetOrderBook();

	Task SendTransactionAsync(SmartTransaction transaction);

	HttpClient CreateHttpClient(string name);

	bool IsForcefulTerminationRequested();
}
