using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using Nito.AsyncEx;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Client.Rpc;
using WalletWasabi.Discoverability;
using WalletWasabi.Extensions;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Helpers;
using WalletWasabi.Io;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Rpc;
using WalletWasabi.Services;
using WalletWasabi.Services.Terminate;
using WalletWasabi.Storages;
using WalletWasabi.Tor;
using WalletWasabi.Tor.Control;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.Banning;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.Wallets;
using WalletWasabi.WebClients.Wasabi;
using static WalletWasabi.Services.Workers;

namespace WalletWasabi.Client;

public class Global
{
	/// <remarks>Use this variable as a guard to prevent touching <see cref="_stoppingCts"/> that might have already been disposed.</remarks>
	private volatile bool _disposeRequested;

	public Global(string dataDir, Config config)
	{
		DataDir = dataDir;
		Config = config;
		TorSettings = new TorSettings(
			DataDir,
			installationFolderPath: EnvironmentHelpers.GetFullBaseDirectory(),
			terminateOnExit: Config.TerminateTorOnExit,
			torMode: Config.UseTor,
			socksPort: config.TorSocksPort,
			controlPort: config.TorControlPort,
			torFolder: config.TorFolder,
			bridges: config.TorBridges,
			owningProcessId: Environment.ProcessId,
			log: Config.LogModes.Contains(LogMode.File));

		EventBus = new EventBus();
		Status = new StatusContainer(EventBus);
		Status.DisposeUsing(_disposables);

		HostedServices = new HostedServices();
		HostedServices.DisposeUsing(_disposables);

		var networkWorkFolderPath = Path.Combine(DataDir, "BitcoinStore", Network.ToString());

		TransactionStore = new AllTransactionStore(networkWorkFolderPath, Network);
		TransactionStore.DisposeUsing(_disposables);

		_bitcoinRpcHttpClientFactory = BuildHttpClientFactory();
		_bitcoinRpcClient = new SwitchableRpcClient(ConfigureBitcoinRpcClient());
		var cpfpProvider = ConfigureCpfpInfoProvider();

		// The wallet learns about the chain and about unconfirmed transactions from the user's node, and from nowhere else.
		var walletFactory = Wallet.CreateFactory(
			Config.Network,
			_bitcoinRpcClient,
			TransactionStore,
			Config.ServiceConfiguration,
			EventBus,
			cpfpProvider,
			watchMempool: !Config.BlockOnlyMode);

		var walletDirectories = new WalletDirectories(Config.Network, DataDir);
		WalletManager = new WalletManager(Config.Network, walletDirectories, walletFactory);

		TransactionBroadcaster = new TransactionBroadcaster(_bitcoinRpcClient, TransactionStore, EventBus);

		// The offer of the wallet is there from the start, for whoever shows it. It does nothing until the user posts an offer.
		_nostrPublisher = new SwitchableNostrPublisher(CreateNostrPublisher(Config, TorSettings));
		RegisterMakerOfferService();

		_ticker = new Timer(_ => EventBus.Publish(new Tick(DateTime.UtcNow)));
		_ticker.DisposeUsing(_disposables);

		_stoppingCts.DisposeUsing(_disposables);
	}

	private readonly AsyncLock _initializationAsyncLock = new();
	private readonly CancellationTokenSource _stoppingCts = new();

	private volatile TorManager? _torManager;
	private readonly HttpClientFactory _bitcoinRpcHttpClientFactory;

	/// <summary>What posts to the Nostr relays. Whoever posts holds this one publisher, so that <see cref="ConnectToNostrRelaysAsync"/> can put other relays behind it.</summary>
	private readonly SwitchableNostrPublisher _nostrPublisher;

	/// <summary>
	/// Creates the HTTP clients that talk to the coordinator, once there is one. Everything that coinjoins holds this
	/// one factory, so that <see cref="ConnectToCoordinatorAsync"/> can put another coordinator behind it.
	/// </summary>
	private SwitchableHttpClientFactory? _coordinatorHttpClientFactory;

	/// <summary>The coordinator that <see cref="_coordinatorHttpClientFactory"/> talks to.</summary>
	private volatile Uri? _coordinatorUri;

	/// <summary>Whether the computer is kept awake during coinjoins. That is asked for when the application is initialized.</summary>
	private bool _initializeSleepInhibitor;
	/// <summary>The user's node. Everything that needs the node holds this one client, so that <see cref="ConnectToBitcoinNodeAsync"/> can put another node behind it.</summary>
	private readonly SwitchableRpcClient _bitcoinRpcClient;
	private CoinPrison? _coinPrison;
	private readonly Timer _ticker;
	private readonly ComposedDisposable _disposables = new();
	private readonly ComposedAsyncDisposable _asyncDisposables = new();

	public StatusContainer Status { get; }

	/// <summary>Creates the HTTP clients that reach the Bitcoin node when it is an onion service.</summary>
	public IHttpClientFactory BitcoinRpcHttpClientFactory => _bitcoinRpcHttpClientFactory;
	public string DataDir { get; }
	public TorSettings TorSettings { get; }

	public AllTransactionStore TransactionStore { get; }
	public Config Config { get; }
	public WalletManager WalletManager { get; }
	public TransactionBroadcaster TransactionBroadcaster { get; }
	public HostedServices HostedServices { get; }
	public Network Network => Config.Network;
	public JsonRpcServer? RpcServer { get; private set; }
	public Uri? OnionServiceUri { get; private set; }
	public EventBus EventBus { get; }

	/// <summary>The Nostr relays that the offers of the wallet are posted to now.</summary>
	public IReadOnlyList<Uri> NostrRelays => _nostrPublisher.Relays;

	/// <summary>The coordinator that the application coinjoins with now, if there is one.</summary>
	public Uri? CoordinatorUri => _coordinatorUri ?? (Config.TryGetCoordinatorUri(out var coordinatorUri) ? coordinatorUri : null);

	/// <summary>How long a node is given to answer when the user asks to be connected to it.</summary>
	private static readonly TimeSpan NodeAnswerTimeout = TimeSpan.FromSeconds(30);

	/// <summary>The same for a node that is an onion service: the way through Tor takes its time.</summary>
	private static readonly TimeSpan OnionNodeAnswerTimeout = TimeSpan.FromSeconds(90);

	/// <summary>How long a node that is an onion service may take until its answer to a request starts to arrive.</summary>
	private static readonly TimeSpan OnionNodeAnswerStartTimeout = TimeSpan.FromSeconds(100);

	/// <summary>How long nothing may arrive while an answer of such a node is on its way, before the connection counts as lost.</summary>
	private static readonly TimeSpan OnionNodeSilenceTimeout = TimeSpan.FromMinutes(2);

	/// <summary>
	/// Creates the client of the RPC interface of the node that the application is configured to use. The node is the only
	/// source of blockchain data, so there is always a client: with settings that do not work it reports what is wrong instead of connecting.
	/// </summary>
	private RPCClient ConfigureBitcoinRpcClient()
	{
		var uriString = string.IsNullOrWhiteSpace(Config.BitcoinRpcUri)
			? RpcSettings.GetDefaultUri(Network)
			: Config.BitcoinRpcUri;

		if (!Uri.TryCreate(uriString, UriKind.Absolute, out var bitcoinRpcUri))
		{
			throw new UriFormatException($"Config property '{nameof(Config.BitcoinRpcUri)}' was set to an invalid URI value: {Config.BitcoinRpcUri}");
		}

		// Without credentials the cookie file of a local node is used.
		var credentials = new RPCCredentialString();
		var credentialString = Config.BitcoinRpcCredentialString;

		if (!string.IsNullOrWhiteSpace(credentialString) && !RPCCredentialString.TryParse(credentialString, out credentials))
		{
			Logger.LogWarning("Failed to parse RPC credentials.");
			credentials = InvalidRpcCredentials();
		}

		RPCClient rpcClient;

		try
		{
			rpcClient = CreateBitcoinRpcClient(credentials, bitcoinRpcUri);
		}
		catch (ArgumentException ex)
		{
			Logger.LogWarning($"Failed to set up Bitcoin RPC using '{bitcoinRpcUri}'.", ex);
			rpcClient = CreateBitcoinRpcClient(InvalidRpcCredentials(), bitcoinRpcUri);
		}

		Logger.LogDebug($"Configured Bitcoin RPC to use {bitcoinRpcUri}.");
		return rpcClient;

		// The node refuses these, which is how the user gets told that the configured credentials are wrong.
		static RPCCredentialString InvalidRpcCredentials() =>
			new() { UserPassword = new NetworkCredential("invalid", "invalid") };
	}

	/// <summary>Creates the client of the RPC interface of a node.</summary>
	private RPCClient CreateBitcoinRpcClient(RPCCredentialString credentials, Uri bitcoinRpcUri)
	{
		var rpcClient = new RPCClient(credentials, bitcoinRpcUri, Network);

		// Use Tor only if the address ends with .onion. Especially, do not use Tor for loopback (i.e. `localhost`).
		if (RpcSettings.IsOnion(bitcoinRpcUri))
		{
			// A block can take minutes to come through Tor, so the answers of the node have no deadline as a whole, like those
			// of a node that is reached directly. Tor can lose a connection without telling, though, so the node has to
			// start answering in time and must not fall silent in the middle of an answer.
			rpcClient.HttpClient = _bitcoinRpcHttpClientFactory.CreateClientForSlowAnswers(
				"long-live-rpc-connection",
				OnionNodeAnswerStartTimeout,
				OnionNodeSilenceTimeout);
		}

		return rpcClient;
	}

	/// <summary>
	/// Makes the application work with another node from now on, without a restart. The node is asked first: one that
	/// does not answer, or that is on another network, is not taken, and the application keeps the node that it has.
	/// </summary>
	/// <param name="rpcUri">The address of the RPC interface of the node, for example <c>http://localhost:8332</c>.</param>
	/// <param name="rpcCredentialString"><c>user:password</c> or <c>cookiefile=path</c>. Nothing stands for the cookie file in the default data folder of Bitcoin Core.</param>
	/// <returns>Nothing if the application works with the node, otherwise the reason why it does not, in words for the user.</returns>
	public async Task<Result<string>> ConnectToBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken)
	{
		var answer = await AskBitcoinNodeAsync(rpcUri, rpcCredentialString, cancellationToken).ConfigureAwait(false);

		if (!answer.IsOk)
		{
			return Result<string>.Fail(answer.Error);
		}

		var rpcClient = answer.Value;

		_bitcoinRpcClient.SwitchTo(rpcClient);
		Logger.LogInfo($"Bitcoin RPC uses {rpcClient.Address.Scheme}://{rpcClient.Address.Authority} from now on.");

		// Everybody learns about the new node now, and not only when the monitor asks it the next time.
		try
		{
			await RpcMonitor.CheckAsync(_bitcoinRpcClient, EventBus, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
		{
			Logger.LogDebug(ex);
		}

		return Result<string>.Ok();
	}

	/// <summary>
	/// Asks a node whether the application could work with it, the way <see cref="ConnectToBitcoinNodeAsync"/> does,
	/// and leaves it at that: the application keeps the node that it has.
	/// </summary>
	/// <returns>Nothing if the application could work with the node, otherwise the reason why it could not, in words for the user.</returns>
	public async Task<Result<string>> VerifyBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken)
	{
		var answer = await AskBitcoinNodeAsync(rpcUri, rpcCredentialString, cancellationToken).ConfigureAwait(false);

		return answer.IsOk ? Result<string>.Ok() : Result<string>.Fail(answer.Error);
	}

	/// <summary>
	/// Asks the node at the given RPC interface: it has to answer, and it has to be on the network of the application.
	/// </summary>
	/// <returns>The client of the node if the application can work with it, otherwise the reason why it cannot, in words for the user.</returns>
	private async Task<Result<RPCClient, string>> AskBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken)
	{
		if (!RpcSettings.TryParseUri(rpcUri, out var bitcoinRpcUri))
		{
			return "The address of the Bitcoin node is not an HTTP address.";
		}

		if (!RpcSettings.TryParseCredentials(rpcCredentialString, out var credentials))
		{
			return "The RPC credentials are neither 'user:password' nor 'cookiefile=path'.";
		}

		var isOnion = RpcSettings.IsOnion(bitcoinRpcUri);

		if (isOnion && Config.UseTor == TorMode.Disabled)
		{
			return "The Bitcoin node is an onion service, and Tor is turned off.";
		}

		RPCClient rpcClient;

		try
		{
			rpcClient = CreateBitcoinRpcClient(credentials, bitcoinRpcUri);
		}
		catch (ArgumentException ex)
		{
			return $"The Bitcoin node cannot be asked with these settings ({ex.Message}).";
		}

		var timeout = isOnion ? OnionNodeAnswerTimeout : NodeAnswerTimeout;
		var check = await RpcConnectionCheck.CheckAsync(new RpcClientBase(rpcClient), Network, timeout, cancellationToken).ConfigureAwait(false);

		return check.IsOk ? rpcClient : check.Error;
	}

	/// <summary>How long Tor is given to start and to connect to the Tor network when the user asks to be connected to it.</summary>
	private static readonly TimeSpan TorStartTimeout = TimeSpan.FromSeconds(90);

	/// <summary>How long Tor is given to tell whether it is connected, when it is asked and not waited for.</summary>
	private static readonly TimeSpan TorAnswerTimeout = TimeSpan.FromSeconds(5);

	/// <summary>How long a coordinator is given to answer when the user asks to be connected to it.</summary>
	private static readonly TimeSpan CoordinatorAnswerTimeout = TimeSpan.FromSeconds(30);

	/// <summary>The same for a coordinator that is reached through Tor.</summary>
	private static readonly TimeSpan CoordinatorAnswerThroughTorTimeout = TimeSpan.FromSeconds(90);

	/// <summary>
	/// Sees to it that the application is connected to Tor: Tor has to run and to have connected to the Tor network. Tor
	/// is started when the application starts, so this waits for it and starts nothing.
	/// </summary>
	/// <returns>Nothing if the application is connected to Tor, otherwise the reason why it is not, in words for the user.</returns>
	public Task<Result<string>> ConnectToTorAsync(CancellationToken cancellationToken) =>
		AskTorAsync(TorStartTimeout, "Tor has not started in time.", cancellationToken);

	/// <summary>
	/// Tells whether the application is connected to Tor right now. Unlike <see cref="ConnectToTorAsync"/>, it does not
	/// wait for a Tor that is still starting.
	/// </summary>
	/// <returns>Nothing if the application is connected to Tor, otherwise the reason why it is not, in words for the user.</returns>
	public Task<Result<string>> VerifyTorAsync(CancellationToken cancellationToken) =>
		AskTorAsync(TorAnswerTimeout, "Tor is not running.", cancellationToken);

	/// <param name="patience">How long Tor is given to be there and to answer.</param>
	/// <param name="notRunningMessage">What to tell when the Tor that the application starts is not there in that time.</param>
	private async Task<Result<string>> AskTorAsync(TimeSpan patience, string notRunningMessage, CancellationToken cancellationToken)
	{
		if (Config.UseTor == TorMode.Disabled)
		{
			return Result<string>.Fail("Tor is turned off.");
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(patience);

		try
		{
			// The application may still be about to start Tor.
			TorManager? torManager;

			while ((torManager = _torManager) is null)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(250), timeoutCts.Token).ConfigureAwait(false);
			}

			var (_, torControlClient) = await torManager.WaitForNextAttemptAsync(timeoutCts.Token).ConfigureAwait(false);

			// A Tor that the application did not start cannot be asked how far it is. It answers on its SOCKS port, which is what there is to know.
			if (torControlClient is null)
			{
				return Result<string>.Ok();
			}

			var bootstrap = await torControlClient.GetBootstrapPhaseAsync(timeoutCts.Token).ConfigureAwait(false);

			return bootstrap.IsBootstrapped
				? Result<string>.Ok()
				: Result<string>.Fail($"Tor runs, but it has not connected to the Tor network yet ({bootstrap.Progress}%).");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return Result<string>.Fail(Config.UseTor == TorMode.EnabledOnlyRunning
				? $"No Tor answers on port {TorSettings.SocksPort} of this computer."
				: notRunningMessage);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Logger.LogDebug(ex);
			return Result<string>.Fail($"Tor cannot be asked ({ex.Message}).");
		}
	}

	/// <summary>
	/// Makes the application post to the given Nostr relays from now on, without a restart, provided that at least one
	/// of them can be reached. Otherwise the application keeps the relays that it has. A connection is opened to every
	/// relay and closed again: nothing is sent to them.
	/// </summary>
	/// <returns>Whether each of the relays could be reached. The application posts to all of them if any of them could.</returns>
	/// <exception cref="ArgumentException">No relay was named.</exception>
	public async Task<IReadOnlyList<RelayReachability>> ConnectToNostrRelaysAsync(IReadOnlyList<Uri> relays, CancellationToken cancellationToken)
	{
		var publisher = CreateNostrPublisher(relays);
		var reachabilities = await publisher.CheckAsync(cancellationToken).ConfigureAwait(false);

		if (reachabilities.Any(relay => relay.IsReachable))
		{
			_nostrPublisher.SwitchTo(publisher);
			Logger.LogInfo($"Offers are posted to {publisher.Relays.Count} Nostr relays from now on. {reachabilities.Count(relay => relay.IsReachable)} of them could be reached.");
		}

		return reachabilities;
	}

	/// <summary>
	/// Finds out whether the given Nostr relays can be reached, the way <see cref="ConnectToNostrRelaysAsync"/> does,
	/// and leaves it at that: the application keeps the relays that it has. Nothing is sent to a relay.
	/// </summary>
	/// <returns>Whether each of the relays could be reached.</returns>
	/// <exception cref="ArgumentException">No relay was named.</exception>
	public async Task<IReadOnlyList<RelayReachability>> VerifyNostrRelaysAsync(IReadOnlyList<Uri> relays, CancellationToken cancellationToken) =>
		await CreateNostrPublisher(relays).CheckAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>Makes what posts to the given relays: through Tor, unless Tor is turned off.</summary>
	/// <exception cref="ArgumentException">No relay was named.</exception>
	private NostrRelayPublisher CreateNostrPublisher(IReadOnlyList<Uri> relays)
	{
		var publisher = new NostrRelayPublisher(relays, Config.UseTor != TorMode.Disabled ? TorSettings.SocksEndpoint : null);

		if (publisher.Relays.Count == 0)
		{
			throw new ArgumentException("There is no relay to connect to.", nameof(relays));
		}

		return publisher;
	}

	/// <summary>
	/// Makes the application coinjoin with the coordinator at the given address from now on, without a restart, provided
	/// that the coordinator answers. Otherwise the application keeps the coordinator that it has, or none.
	/// </summary>
	/// <remarks>
	/// The coordinator is not changed while a coinjoin is under way: its round is with the coordinator that the
	/// application has. The list of the coins that a coordinator banned stays the one of the coordinator that the
	/// application had first, until the application is started again.
	/// </remarks>
	/// <returns>Nothing if the application works with the coordinator, otherwise the reason why it does not, in words for the user.</returns>
	public async Task<Result<string>> ConnectToCoordinatorAsync(string coordinatorUri, CancellationToken cancellationToken)
	{
		var answer = await AskCoordinatorAsync(coordinatorUri, cancellationToken).ConfigureAwait(false);

		if (!answer.IsOk)
		{
			return Result<string>.Fail(answer.Error);
		}

		var uri = answer.Value;

		using (await _initializationAsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			if (_disposeRequested)
			{
				return Result<string>.Fail($"{Constants.AppName} is shutting down.");
			}

			var coinJoinState = HostedServices.GetOrDefault<CoinJoinManager>()?.HighestCoinJoinClientState ?? CoinJoinClientState.Idle;

			switch (CoordinatorConnectionCheck.DecideChange(_coordinatorUri, uri, coinJoinState))
			{
				case CoordinatorChange.Refuse:
					return Result<string>.Fail($"A coinjoin is under way with the coordinator that {Constants.AppName} has. Stop it or wait for it to end, and connect then.");

				case CoordinatorChange.Switch when _coordinatorHttpClientFactory is { } current:
					current.SwitchTo(CreateCoordinatorHttpClientFactory(uri));
					_coordinatorUri = uri;
					break;

				case CoordinatorChange.Start:
					// The services live as long as the application, not as long as whoever asked for the connection waits.
					await HostedServices.RegisterAndStartAsync<CoinJoinManager>(PrepareCoinJoinComponents(uri, CreateCoordinatorHttpClientFactory(uri)), "CoinJoin Manager", _stoppingCts.Token).ConfigureAwait(false);

					if (_initializeSleepInhibitor)
					{
						await CreateSleepInhibitorAsync().ConfigureAwait(false);
					}

					// A wallet that is open already gets its coinjoins now.
					EventBus.Publish(new CoinJoinAvailable());
					break;
			}
		}

		Logger.LogInfo($"Coinjoins use the coordinator {uri.Scheme}://{uri.Authority} from now on.");
		return Result<string>.Ok();
	}

	/// <summary>
	/// Asks the coordinator at the given address whether it answers, the way <see cref="ConnectToCoordinatorAsync"/>
	/// does, and leaves it at that: the application keeps the coordinator that it has, or none.
	/// </summary>
	/// <returns>Nothing if the coordinator answers, otherwise the reason why it does not, in words for the user.</returns>
	public async Task<Result<string>> VerifyCoordinatorAsync(string coordinatorUri, CancellationToken cancellationToken)
	{
		var answer = await AskCoordinatorAsync(coordinatorUri, cancellationToken).ConfigureAwait(false);

		return answer.IsOk ? Result<string>.Ok() : Result<string>.Fail(answer.Error);
	}

	/// <summary>Asks the coordinator at the given address for its rounds.</summary>
	/// <returns>The address of the coordinator if it answers, otherwise the reason why it does not, in words for the user.</returns>
	private async Task<Result<Uri, string>> AskCoordinatorAsync(string coordinatorUri, CancellationToken cancellationToken)
	{
		if (!CoordinatorConnectionCheck.TryParseUri(coordinatorUri, out var uri))
		{
			return "The address of the coordinator is not an HTTP address.";
		}

		var usesTor = Config.UseTor != TorMode.Disabled;

		if (!usesTor && CoordinatorConnectionCheck.IsOnion(uri))
		{
			return "The coordinator is an onion service, and Tor is turned off.";
		}

		// The check tells what went wrong at once. The clients that coinjoin try many times instead.
		var checkConfig = new HttpClientHandlerConfiguration
		{
			MaxAttempts = 2,
			TimeBeforeRetryingAfterNetworkError = TimeSpan.FromSeconds(0.5)
		};
		var checkClient = new WabiSabiHttpApiClient("satoshi-coordination", new CoordinatorHttpClientFactory(uri, BuildHttpClientFactory(checkConfig)));
		var timeout = usesTor ? CoordinatorAnswerThroughTorTimeout : CoordinatorAnswerTimeout;
		var check = await CoordinatorConnectionCheck.CheckAsync(checkClient, timeout, cancellationToken).ConfigureAwait(false);

		return check.IsOk ? uri : check.Error;
	}

	private HttpClientFactory BuildHttpClientFactory(HttpClientHandlerConfiguration? config = null) =>
		Config.UseTor != TorMode.Disabled
			? new OnionHttpClientFactory(TorSettings.SocksEndpoint.ToUri("socks5"), config)
			: new HttpClientFactory(config);

	private void ConfigureFeeRateUpdater(CancellationToken cancellationToken)
	{
		// The fee rate estimations come from the user's node. They are good for a quarter of an hour.
		// While the node cannot be asked, it is tried again every few seconds: there is nobody else to ask.
		var feeRateUpdater = Spawn("FeeRateUpdater",
			Service("Mining Fee Rate Updater",
				Periodically(
					TimeSpan.FromSeconds(10),
					FeeRateEstimations.Empty,
					FeeRateEstimationUpdater.CreateUpdater(FeeRateProviders.RpcAsync(_bitcoinRpcClient), EventBus, refreshPeriod: TimeSpan.FromMinutes(15)))), cancellationToken);
		feeRateUpdater.DisposeUsing(_disposables);
		EventBus.Subscribe<Tick>(_ => feeRateUpdater.Post(new FeeRateEstimationUpdater.UpdateMessage()))
			.DisposeUsing(_disposables);
	}

	private void ConfigureRpcMonitor(CancellationToken cancellationToken)
	{
		var rpcMonitor = Spawn(RpcMonitor.ServiceName,
			Service("Bitcoin Rpc Interface Monitoring",
				Periodically(
					TimeSpan.FromSeconds(7),
					Unit.Instance,
					RpcMonitor.CreateChecker(_bitcoinRpcClient, EventBus))), cancellationToken);
		rpcMonitor.DisposeUsing(_disposables);
		EventBus.Subscribe<Tick>(_ => rpcMonitor.Post(new RpcMonitor.CheckMessage()))
			.DisposeUsing(_disposables);

		// The node tells the height of the chain.
		EventBus.Subscribe<RpcStatusChanged>(e =>
		{
			if (e.Status.IsOk)
			{
				EventBus.Publish(new NetworkTipHeightChanged((uint)e.Status.Value.Headers));
			}
		}).DisposeUsing(_disposables);
	}

	private CpfpInfoProvider ConfigureCpfpInfoProvider()
	{
		var cpfpUpdater = Spawn("CpfpInfoProvider",
			Service("Cpfp Info provider",
				EventDriven(
					Unit.Instance,
					CpfpInfoUpdater.Create(_bitcoinRpcClient, EventBus))),
			_stoppingCts.Token);
		cpfpUpdater.DisposeUsing(_disposables);

		// A new block or a new transaction of the wallet can change what the unconfirmed transactions effectively pay.
		EventBus.Subscribe<WalletSyncStatusChanged>(e =>
		{
			if (e.Status is WalletSyncStatus.Synchronized)
			{
				cpfpUpdater.Post(new CpfpInfoMessage.UpdateMessage());
			}
		}).DisposeUsing(_disposables);
		EventBus.Subscribe<WalletRelevantTransactionProcessed>(_ => cpfpUpdater.Post(new CpfpInfoMessage.UpdateMessage()))
			.DisposeUsing(_disposables);
		return new CpfpInfoProvider(cpfpUpdater);
	}

	private async Task InitializeBitcoinStoreAsync(CancellationToken cancellationToken)
	{
		try
		{
			await TransactionStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Logger.LogError($"Bitcoin storage got corrupted. Resetting the wallet to the first block to rescan. Exception: {ex}");
			WalletManager.RescanWallet();
			throw;
		}
	}

	public async Task InitializeAsync(bool initializeSleepInhibitor, TerminateService terminateService, CancellationToken cancellationToken)
	{
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
		CancellationToken linkedCtsToken = linkedCts.Token;

		ConfigureRpcMonitor(linkedCtsToken);
		ConfigureFeeRateUpdater(linkedCtsToken);

		// _stoppingCts may be disposed at this point, so do not forward the cancellation token here.
		using (await _initializationAsyncLock.LockAsync(linkedCtsToken))
		{
			Logger.LogTrace("Initialization started.");

			await Task.WhenAll(
				StartTorProcessManagerAsync(linkedCtsToken),
				InitializeBitcoinStoreAsync(linkedCtsToken))
				.ConfigureAwait(false);

			if (_disposeRequested)
			{
				return;
			}

			try
			{
				_initializeSleepInhibitor = initializeSleepInhibitor;

				// The user may have connected the application to a coordinator already, see ConnectToCoordinatorAsync.
				if (_coordinatorHttpClientFactory is null && Config.TryGetCoordinatorUri(out var coordinatorUri))
				{
					HostedServices.Register<CoinJoinManager>(PrepareCoinJoinComponents(coordinatorUri, CreateCoordinatorHttpClientFactory(coordinatorUri)), "CoinJoin Manager");

					// The wallet may have been opened while Tor was still starting.
					EventBus.Publish(new CoinJoinAvailable());
				}

				if (_coordinatorHttpClientFactory is not null && initializeSleepInhibitor && HostedServices.GetOrDefault<SleepInhibitor>() is null)
				{
					await CreateSleepInhibitorAsync().ConfigureAwait(false);
				}

				await HostedServices.StartAllAsync(linkedCtsToken).ConfigureAwait(false);

				await StartRpcServerAsync(terminateService, linkedCtsToken).ConfigureAwait(false);
			}
			finally
			{
				Logger.LogTrace("Initialization finished.");
			}
			_ticker.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
		}
	}

	private async Task CreateSleepInhibitorAsync()
	{
		SleepInhibitor? sleepInhibitor = await SleepInhibitor.CreateAsync(HostedServices.Get<CoinJoinManager>()).ConfigureAwait(false);

		if (sleepInhibitor is not null)
		{
			// It is started with the other services, or right away if they run already.
			await HostedServices.RegisterAndStartAsync<SleepInhibitor>(() => sleepInhibitor, "Sleep Inhibitor", _stoppingCts.Token).ConfigureAwait(false);
		}
		else
		{
			Logger.LogInfo("Sleep Inhibitor is not available on this platform.");
		}
	}

	private async Task StartRpcServerAsync(TerminateService terminateService, CancellationToken cancel)
	{
		// HttpListener cannot listen on an onion address, so the onion service forwards to this loopback port.
		var prefixes = OnionServiceUri is { }
			? Config.JsonRpcServerPrefixes.Append("http://127.0.0.1:37129/").ToArray()
			: Config.JsonRpcServerPrefixes;

		var jsonRpcServerConfig = new JsonRpcServerConfiguration(Config.JsonRpcServerEnabled, Config.JsonRpcUser, Config.JsonRpcPassword, prefixes, Config.Network);
		if (jsonRpcServerConfig.IsEnabled)
		{
			var wasabiJsonRpcService = new WasabiJsonRpcService(global: this);
			RpcServer = new JsonRpcServer(wasabiJsonRpcService, jsonRpcServerConfig, terminateService);
			RpcServer.DisposeUsing(_disposables);
			try
			{
				await RpcServer.StartAsync(cancel).ConfigureAwait(false);
				RpcServer.DisposeUsing(_disposables);
			}
			catch (HttpListenerException e)
			{
				Logger.LogWarning($"Failed to start {nameof(JsonRpcServer)} with error: {e.Message}.");
				RpcServer = null;
			}
		}
	}

	private async Task StartTorProcessManagerAsync(CancellationToken cancellationToken)
	{
		if (Config.UseTor != TorMode.Disabled)
		{
			TorProcessManager processManager = new(EventBus);
			_torManager = new TorManager(TorSettings, processManager);
			_torManager.DisposeUsing(_asyncDisposables);
			await _torManager.StartAsync(attempts: 3, cancellationToken).ConfigureAwait(false);
			Logger.LogInfo($"{nameof(TorManager)} is initialized.");

			var (_, torControlClient) = await _torManager.WaitForNextAttemptAsync(cancellationToken).ConfigureAwait(false);
			if (Config is { JsonRpcServerEnabled: true, RpcOnionEnabled: true } && torControlClient is { } nonNullTorControlClient)
			{
				var anonymousAccessAllowed = string.IsNullOrEmpty(Config.JsonRpcUser) || string.IsNullOrEmpty(Config.JsonRpcPassword);
				if (!anonymousAccessAllowed)
				{
					var onionServiceId = await nonNullTorControlClient.CreateEphemeralOnionServiceAsync(80, 37129, cancellationToken).ConfigureAwait(false);
					OnionServiceUri = new Uri($"http://{onionServiceId}.onion");
					Logger.LogInfo($"RPC server listening on {OnionServiceUri}");
				}
				else
				{
					Logger.LogInfo("Anonymous access RPC server cannot be exposed as onion service.");
				}
			}
		}
	}

	/// <summary>Creates the factory of the HTTP clients that coinjoin with a coordinator.</summary>
	private CoordinatorHttpClientFactory CreateCoordinatorHttpClientFactory(Uri coordinatorUri)
	{
		// Aggressively retry
		var coordinatorHttpClientConfig = new HttpClientHandlerConfiguration
		{
			MaxAttempts = 10,
			TimeBeforeRetryingAfterNetworkError = TimeSpan.FromSeconds(0.5),
			TimeBeforeRetryingAfterServerError = TimeSpan.FromSeconds(0.5),
			TimeBeforeRetryingAfterTooManyRequests = TimeSpan.FromSeconds(0.1)
		};

		return new CoordinatorHttpClientFactory(coordinatorUri, BuildHttpClientFactory(coordinatorHttpClientConfig));
	}

	/// <summary>
	/// Sets up what the coinjoins with a coordinator need, and gives what creates the service that manages them. This
	/// is done once: another coordinator is put behind <see cref="_coordinatorHttpClientFactory"/> afterwards.
	/// </summary>
	private Func<CoinJoinManager> PrepareCoinJoinComponents(Uri coordinatorUri, CoordinatorHttpClientFactory httpClientFactory)
	{
		var prisonForCoordinator = Path.Combine(DataDir, coordinatorUri.Host);
		_coinPrison = CoinPrison.CreateOrLoadFromFile(prisonForCoordinator);
		_coinPrison.DisposeUsing(_disposables);

		EventBus
			.Subscribe<WalletLoaded>(e => _coinPrison.UpdateWallet(e.Wallet))
			.DisposeUsing(_disposables);

		var coordinatorHttpClientFactory = new SwitchableHttpClientFactory(httpClientFactory);
		_coordinatorHttpClientFactory = coordinatorHttpClientFactory;
		_coordinatorUri = coordinatorUri;

		var wabiSabiStatusProvider =  new WabiSabiHttpApiClient("satoshi-coordination", coordinatorHttpClientFactory);
		var roundUpdater = Spawn("RoundUpdater",
			Service("WabiSabi Rounds Updater",
				EventDriven(
					new RoundsState(DateTime.UtcNow, RoundStateProvider.QueryFrequency, new Dictionary<uint256, RoundState>(), []),
					RoundStateUpdater.Create(wabiSabiStatusProvider))),
			_stoppingCts.Token);
		roundUpdater.DisposeUsing(_disposables);
		EventBus.Subscribe<Tick>(_ => roundUpdater.Post(new RoundUpdateMessage.UpdateMessage(DateTime.UtcNow)))
			.DisposeUsing(_disposables);

		Func<string, WabiSabiHttpApiClient> wabiSabiHttpClientFactory = (identity) => new WabiSabiHttpApiClient(identity, coordinatorHttpClientFactory);
		var coinJoinConfiguration = new CoinJoinConfiguration(Config.CoordinatorIdentifier, Config.MaxCoinjoinMiningFeeRate, Config.AbsoluteMinInputCount, AllowSoloCoinjoining: false);
		var coinPrison = _coinPrison;

		return () => new CoinJoinManager(GetCoinJoinWalletsAsync, new RoundStateProvider(roundUpdater), wabiSabiHttpClientFactory, coinJoinConfiguration, coinPrison, CreateInputVerifier(), EventBus);
	}

	/// <summary>
	/// The service that keeps the offer of the wallet on the Nostr relays. It contacts no relay until the user posts an offer.
	/// </summary>
	private void RegisterMakerOfferService()
	{
		HostedServices.Register<MakerOfferService>(
			() => new MakerOfferService(
				Network,
				_nostrPublisher,
				() => WalletManager.Wallet?.Coins,
				EventBus,
				offerKey => WalletManager.Wallet?.GetBondBacking(offerKey)),
			"Maker Offer");

		// A transaction of the wallet may have spent a coin that is on offer.
		var makerOfferService = HostedServices.Get<MakerOfferService>();
		EventBus.Subscribe<WalletRelevantTransactionProcessed>(_ => makerOfferService.TriggerRound())
			.DisposeUsing(_disposables);

		// The offers of the other makers are read from the same relays, when somebody looks at the order book. The
		// fidelity bonds that they name are looked up with the node.
		OrderBook = new OrderBookService(
			Network,
			_nostrPublisher,
			new BondProofVerifier(_bitcoinRpcClient),
			() => [.. makerOfferService.Offers.Select(offer => offer.PublicKey)],
			EventBus);
	}

	/// <summary>The offers of the other makers, as the Nostr relays tell them. The relays are asked when this is asked to.</summary>
	public OrderBookService OrderBook { get; private set; } = null!;

	/// <summary>
	/// Makes what posts to the Nostr relays of the configuration. The relays are reached through Tor and in no other way,
	/// unless Tor is turned off: then they are reached directly, like everything else.
	/// </summary>
	/// <exception cref="FormatException">The configuration names something that is not the address of a relay.</exception>
	public static NostrRelayPublisher CreateNostrPublisher(Config config, TorSettings torSettings) =>
		new(NostrRelayPublisher.ParseRelays(config.NostrRelays), config.UseTor != TorMode.Disabled ? torSettings.SocksEndpoint : null);

	/// <summary>The wallets that the coinjoin manager looks after: the wallet of the application, once there is one.</summary>
	private Task<IEnumerable<Wallet>> GetCoinJoinWalletsAsync() =>
		Task.FromResult<IEnumerable<Wallet>>(WalletManager.Wallet is { } wallet ? [wallet] : []);

	private InputVerifier CreateInputVerifier()
	{
		Logger.LogInfo("Using Bitcoin RPC for coinjoin input verification (10% sample).");
		return InputVerifiers.CreateRpcVerifier(_bitcoinRpcClient);
	}

	public async Task DisposeAsync()
	{
		// Dispose method may be called just once.
		if (!_disposeRequested)
		{
			_disposeRequested = true;
			await _stoppingCts.CancelAsync().ConfigureAwait(false);
		}
		else
		{
			return;
		}

		using (await _initializationAsyncLock.LockAsync())
		{
			Logger.LogWarning("Process is exiting.");

			try
			{
				try
				{
					using var dequeueCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
					await WalletManager.RemoveAndStopAsync(dequeueCts.Token).ConfigureAwait(false);
					Logger.LogInfo($"{nameof(WalletManager)} is stopped.");
				}
				catch (Exception ex)
				{
					Logger.LogError($"Error during {nameof(WalletManager.RemoveAndStopAsync)}: {ex}");
				}

				if (RpcServer is { } rpcServer)
				{
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(21));
					await rpcServer.StopAsync(cts.Token).ConfigureAwait(false);
					Logger.LogInfo($"{nameof(RpcServer)} is stopped.");
				}

				if (HostedServices is { } backgroundServices)
				{
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(21));
					await backgroundServices.StopAllAsync(cts.Token).ConfigureAwait(false);
					Logger.LogInfo("Stopped background services.");
				}

				if (_torManager is not null)
				{
					using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

					var torControlClient =
						Result<(CancellationToken, TorControlClient), Exception>
						.Catch(async () => await _torManager.WaitForNextAttemptAsync(cts.Token).ConfigureAwait(false))
						.Map(x => x.Result.Item2)
						.AsNullable();

					if (OnionServiceUri is { } nonNullOnionServiceUri && torControlClient is { } nonNullTorControlClient)
					{
						try
						{
							var isDestroyedSuccessfully = await nonNullTorControlClient
								.DestroyOnionServiceAsync(nonNullOnionServiceUri.Host, cts.Token).ConfigureAwait(false);
							if (!isDestroyedSuccessfully)
							{
								Logger.LogInfo($"Onion service '{nonNullOnionServiceUri.Host}' failed to be destroyed.");
							}
						}
						catch (OperationCanceledException)
						{
							Logger.LogInfo($"'{nonNullOnionServiceUri.Host}' failed to be stopped in allotted time.");
						}
					}

					Logger.LogInfo("TorManager is stopped.");
				}

				_disposables.Dispose();
				await _asyncDisposables.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex);
			}
			finally
			{
				Logger.LogTrace("Dispose finished.");
			}
		}
	}
}
