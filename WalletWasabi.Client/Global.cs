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
			wasabiInstallationFolderPath: EnvironmentHelpers.GetFullBaseDirectory(),
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

		BitcoinRpcHttpClientFactory = BuildHttpClientFactory();
		_bitcoinRpcClient = ConfigureBitcoinRpcClient();
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

		TransactionBroadcaster = new TransactionBroadcaster(_bitcoinRpcClient, EventBus);

		_ticker = new Timer(_ => EventBus.Publish(new Tick(DateTime.UtcNow)));
		_ticker.DisposeUsing(_disposables);

		_stoppingCts.DisposeUsing(_disposables);
	}

	private readonly AsyncLock _initializationAsyncLock = new();
	private readonly CancellationTokenSource _stoppingCts = new();

	private TorManager? _torManager;
	private readonly IRPCClient _bitcoinRpcClient;
	private CoinPrison? _coinPrison;
	private readonly Timer _ticker;
	private readonly ComposedDisposable _disposables = new();
	private readonly ComposedAsyncDisposable _asyncDisposables = new();

	public StatusContainer Status { get; }

	/// <summary>Creates the HTTP clients that reach the Bitcoin node when it is an onion service.</summary>
	public IHttpClientFactory BitcoinRpcHttpClientFactory { get; }
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

	/// <summary>
	/// Creates the client of the RPC interface of the user's node. The node is the only source of blockchain data,
	/// so there is always a client: with settings that do not work it reports what is wrong instead of connecting.
	/// </summary>
	private RpcClientBase ConfigureBitcoinRpcClient()
	{
		var uriString = string.IsNullOrWhiteSpace(Config.BitcoinRpcUri)
			? GetDefaultBitcoinRpcUri()
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

		RPCClient internalRpcClient;

		try
		{
			internalRpcClient = new RPCClient(credentials, bitcoinRpcUri, Network);
		}
		catch (ArgumentException ex)
		{
			Logger.LogWarning($"Failed to set up Bitcoin RPC using '{bitcoinRpcUri}'.", ex);
			internalRpcClient = new RPCClient(InvalidRpcCredentials(), bitcoinRpcUri, Network);
		}

		// Use Tor only if the address ends with .onion. Especially, do not use Tor for loopback (i.e. `localhost`).
		if (bitcoinRpcUri.DnsSafeHost.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
		{
			internalRpcClient.HttpClient = BitcoinRpcHttpClientFactory.CreateClient("long-live-rpc-connection");
		}

		Logger.LogDebug($"Configured Bitcoin RPC to use {bitcoinRpcUri}.");
		return new RpcClientBase(internalRpcClient);

		// The node refuses these, which is how the user gets told that the configured credentials are wrong.
		static RPCCredentialString InvalidRpcCredentials() =>
			new() { UserPassword = new NetworkCredential("invalid", "invalid") };
	}

	private string GetDefaultBitcoinRpcUri() =>
		Network == Network.Main ? Constants.DefaultMainNetBitcoinRpcUri
		: Network == Network.TestNet ? Constants.DefaultTestNetBitcoinRpcUri
		: Network == Network.RegTest ? Constants.DefaultRegTestBitcoinRpcUri
		: Constants.DefaultSignetBitcoinRpcUri;

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
				if (Config.TryGetCoordinatorUri(out var coordinatorUri))
				{
					RegisterCoinJoinComponents(coordinatorUri);

					if (initializeSleepInhibitor)
					{
						await CreateSleepInhibitorAsync().ConfigureAwait(false);
					}
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
			HostedServices.Register<SleepInhibitor>(() => sleepInhibitor, "Sleep Inhibitor");
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

	private void RegisterCoinJoinComponents(Uri coordinatorUri)
	{
		var prisonForCoordinator = Path.Combine(DataDir, coordinatorUri.Host);
		_coinPrison = CoinPrison.CreateOrLoadFromFile(prisonForCoordinator);
		_coinPrison.DisposeUsing(_disposables);

		EventBus
			.Subscribe<WalletLoaded>(e => _coinPrison.UpdateWallet(e.Wallet))
			.DisposeUsing(_disposables);

		// Aggressively retry
		var coordinatorHttpClientConfig = new HttpClientHandlerConfiguration
		{
			MaxAttempts = 10,
			TimeBeforeRetryingAfterNetworkError = TimeSpan.FromSeconds(0.5),
			TimeBeforeRetryingAfterServerError = TimeSpan.FromSeconds(0.5),
			TimeBeforeRetryingAfterTooManyRequests = TimeSpan.FromSeconds(0.1)
		};
		var coordinatorHttpClientFactory = new CoordinatorHttpClientFactory(coordinatorUri, BuildHttpClientFactory(coordinatorHttpClientConfig));

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
		HostedServices.Register<CoinJoinManager>(() => new CoinJoinManager(GetCoinJoinWalletsAsync, new RoundStateProvider(roundUpdater), wabiSabiHttpClientFactory, coinJoinConfiguration, _coinPrison, CreateInputVerifier(), EventBus), "CoinJoin Manager");
	}

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
