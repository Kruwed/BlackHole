using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Client;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Discoverability;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Helpers;
using WalletWasabi.Services;
using WalletWasabi.Services.Terminate;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent;

public class Services : IServices
{
	// Temporary solution. It should be removed
	public static Services Instance { get; private set; } = null!;

	private readonly Global _global;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly TransactionBroadcaster _transactionBroadcaster;
	private readonly HostedServices _hostedServices;
	private readonly TerminateService _terminateService;
	private readonly StatusContainer _status;

	private Services(Global global, UiConfig uiConfig, TerminateService terminateService)
	{
		Guard.NotNull(nameof(global.DataDir), global.DataDir);
		Guard.NotNull(nameof(global.BitcoinRpcHttpClientFactory), global.BitcoinRpcHttpClientFactory);
		Guard.NotNull(nameof(global.Config), global.Config);
		Guard.NotNull(nameof(global.WalletManager), global.WalletManager);
		Guard.NotNull(nameof(global.TransactionBroadcaster), global.TransactionBroadcaster);
		Guard.NotNull(nameof(global.HostedServices), global.HostedServices);
		Guard.NotNull(nameof(uiConfig), uiConfig);
		Guard.NotNull(nameof(terminateService), terminateService);

		_global = global;
		_httpClientFactory = global.BitcoinRpcHttpClientFactory;
		_transactionBroadcaster = global.TransactionBroadcaster;
		_hostedServices = global.HostedServices;
		_terminateService = terminateService;
		_status = global.Status;
		DataDir = global.DataDir;
		PersistentConfig = global.Config.PersistentConfig;
		WalletManager = global.WalletManager;
		UiConfig = uiConfig;
		Config = global.Config;
		EventBus = global.EventBus;
	}

	public string DataDir { get; }
	public string PersistentConfigFilePath => Path.Combine(DataDir, PersistentConfig.GetConfigFileName());
	public PersistentConfig PersistentConfig { get; }
	public WalletManager WalletManager { get; }
	public UiConfig UiConfig { get; }
	public Config Config { get; }
	public EventBus EventBus { get; }

	// Chain info
	public uint GetNodeTipHeight() => _status.BestNetworkHeight;
	public ConnectedRpcStatus? GetNodeStatus() => _status.BitcoinRpcStatus is { IsOk: true } status ? status.Value : null;
	public bool? IsNodeAnswering() => _status.BitcoinRpcStatus?.IsOk;
	public FeeRateEstimations? GetFeeRateEstimations() => _status.FeeRates;
	public Task<Result<string>> ConnectToBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken) =>
		_global.ConnectToBitcoinNodeAsync(rpcUri, rpcCredentialString, cancellationToken);
	public Task<Result<string>> VerifyBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken) =>
		_global.VerifyBitcoinNodeAsync(rpcUri, rpcCredentialString, cancellationToken);
	public Task<Result<string>> ConnectToTorAsync(CancellationToken cancellationToken) =>
		_global.ConnectToTorAsync(cancellationToken);
	public Task<Result<string>> VerifyTorAsync(CancellationToken cancellationToken) =>
		_global.VerifyTorAsync(cancellationToken);

	public Task<int?> CountTorRelayAddressesAsync(CancellationToken cancellationToken) =>
		_global.CountTorRelayAddressesAsync(cancellationToken);
	public Task<IReadOnlyList<RelayReachability>> ConnectToNostrRelaysAsync(IReadOnlyList<Uri> relays, CancellationToken cancellationToken) =>
		_global.ConnectToNostrRelaysAsync(relays, cancellationToken);
	public Task<IReadOnlyList<RelayReachability>> VerifyNostrRelaysAsync(IReadOnlyList<Uri> relays, CancellationToken cancellationToken) =>
		_global.VerifyNostrRelaysAsync(relays, cancellationToken);
	public IReadOnlyList<Uri> GetNostrRelays() => _global.NostrRelays;
	public Task<Result<string>> ConnectToCoordinatorAsync(string coordinatorUri, CancellationToken cancellationToken) =>
		_global.ConnectToCoordinatorAsync(coordinatorUri, cancellationToken);
	public Task<Result<string>> VerifyCoordinatorAsync(string coordinatorUri, CancellationToken cancellationToken) =>
		_global.VerifyCoordinatorAsync(coordinatorUri, cancellationToken);
	public Uri? GetCoordinatorUri() => _global.CoordinatorUri;
	public uint GetTipHeight() => WalletManager.GetBestHeight()?.Height ?? 0;

	// WalletManager info
	public Network GetNetwork() => WalletManager.Network;
	public bool HasWallet() => WalletManager.HasWallet;
	public Task StartWalletAsync(Wallet wallet) => WalletManager.StartWalletAsync(wallet);
	public void AddWallet(KeyManager keyManager) => WalletManager.AddWallet(keyManager);

	// Tor info
	public TorMode GetUseTor() => Config.UseTor;

	// UI Config
	public double? GetWindowWidth() => UiConfig.WindowWidth;
	public double? GetWindowHeight() => UiConfig.WindowHeight;
	public void SetWindowWidth(double? width) => UiConfig.WindowWidth = width;
	public void SetWindowHeight(double? height) => UiConfig.WindowHeight = height;
	public int GetFeeTarget() => UiConfig.FeeTarget;
	public void SetFeeTarget(int value) => UiConfig.FeeTarget = value;

	// Temporary solution
	public T? GetHostedService<T>() where T : class, Microsoft.Extensions.Hosting.IHostedService => _hostedServices.GetOrDefault<T>();

	// Transaction
	public WalletWasabi.JoinMarket.OrderBookService GetOrderBook() => _global.OrderBook;

	public Task SendTransactionAsync(SmartTransaction transaction) => _transactionBroadcaster.SendTransactionAsync(transaction);

	// HttpClientFactory wrapper functions
	public HttpClient CreateHttpClient(string name) => _httpClientFactory.CreateClient(name);

	// TerminateService wrapper functions
	public bool IsForcefulTerminationRequested() => _terminateService.ForcefulTerminationRequestedTask.IsCompletedSuccessfully;

	public static Services Create(Global global, UiConfig uiConfig, TerminateService terminateService)
	{
		Instance = new Services(global, uiConfig, terminateService);
		return Instance;
	}
}
