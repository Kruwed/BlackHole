using Microsoft.Extensions.Hosting;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using static WalletWasabi.Logging.LoggerTools;

namespace WalletWasabi.Wallets;

public delegate Wallet WalletFactory(KeyManager keyManager);

public class Wallet : BackgroundService
{
	private readonly ComposedDisposable _disposables = new();

	/// <param name="rpcClient">The RPC interface of the user's node. The wallet learns everything about the chain from it.</param>
	/// <param name="watchMempool">Whether the wallet looks at the unconfirmed transactions of the node too, or only at its blocks.</param>
	public static WalletFactory CreateFactory(
		Network network, IRPCClient rpcClient, AllTransactionStore transactionStore,
		ServiceConfiguration serviceConfiguration, EventBus eventBus, CpfpInfoProvider cpfpInfoProvider, bool watchMempool = true) =>
		keyManager => new Wallet(network, keyManager, rpcClient, transactionStore, serviceConfiguration, cpfpInfoProvider, eventBus, watchMempool);

	private Wallet(
		Network network,
		KeyManager keyManager,
		IRPCClient rpcClient,
		AllTransactionStore transactionStore,
		ServiceConfiguration serviceConfiguration,
		CpfpInfoProvider cpfpInfoProvider,
		EventBus eventBus,
		bool watchMempool)
	{
		Password = "";
		Network = network;
		KeyManager = keyManager;
		ServiceConfiguration = serviceConfiguration;
		CpfpInfoProvider = cpfpInfoProvider;
		DestinationProvider = new InternalDestinationProvider(KeyManager);
		TransactionStore = transactionStore;

		TransactionProcessor = new TransactionProcessor(TransactionStore, keyManager, eventBus);
		Synchronizer = new WalletSynchronizer(rpcClient, keyManager, TransactionStore, TransactionProcessor, eventBus, onSynchronizedFirst: LoadDummyMempool, watchMempool: watchMempool);
		Coins = TransactionProcessor.Coins;
		BatchedPayments = new PaymentBatch(Coins);
		OutputProvider = new PaymentAwareOutputProvider(DestinationProvider, BatchedPayments, RandomnessProviders.Secure);
		_eventBus = eventBus;
		WalletId = new WalletId(Guid.NewGuid());

		_eventBus.Subscribe<MiningFeeRatesChanged>(e => FeeRateEstimations = e.AllFeeEstimate)
			.DisposeUsing(_disposables);

		_eventBus.Subscribe<WalletRelevantTransactionProcessed>(e => WalletRelevantTransactionProcessed(e.Result))
			.DisposeUsing(_disposables);

		_eventBus.Subscribe<NewTransactionInMempool>(e => Mempool_TransactionReceived(e.Transaction))
			.DisposeUsing(_disposables);
	}

	private readonly EventBus _eventBus;
	public AllTransactionStore TransactionStore { get; }

	public WalletId WalletId { get; }
	public bool Loaded { get; private set; }
	public KeyManager KeyManager { get; }
	public ServiceConfiguration ServiceConfiguration { get; }
	public FeeRateEstimations? FeeRateEstimations { get; private set; }

	public CoinsRegistry Coins { get; }

	public bool NonPrivateCoinIsolation => KeyManager.NonPrivateCoinIsolation;

	public bool OnlyUsePrivateFundsForPayments => KeyManager.OnlyUsePrivateFundsForPayments;

	public Network Network { get; }
	public TransactionProcessor TransactionProcessor { get; }

	public CpfpInfoProvider CpfpInfoProvider { get; }

	/// <summary>Keeps the wallet up to date with the user's node.</summary>
	public WalletSynchronizer Synchronizer { get; }

	public bool IsLoggedIn { get; private set; }
	public string Password { get; set; }

	public IKeyChain? KeyChain { get; private set; }

	public IDestinationProvider DestinationProvider { get; }

	public OutputProvider OutputProvider { get; }
	public PaymentBatch BatchedPayments { get; }

	public int AnonScoreTarget => KeyManager.AnonScoreTarget;

	public Money PlebStopThreshold => KeyManager.PlebStopThreshold;

	public ICoinsView GetAllCoins() => Coins.AsAllCoinsView();

	public bool IsWalletPrivate() => GetPrivacyPercentage() >= 100;

	public IEnumerable<SmartCoin> GetCoinjoinCoinCandidates() => Coins;

	/// <summary>
	/// Get all the transactions associated to the wallet ordered by blockchain.
	/// </summary>
	public IEnumerable<SmartTransaction> GetTransactions()
	{
		var walletTransactions = new HashSet<SmartTransaction>();

		foreach (SmartCoin coin in GetAllCoins())
		{
			walletTransactions.Add(coin.Transaction);
			if (coin.SpenderTransaction is not null)
			{
				walletTransactions.Add(coin.SpenderTransaction);
			}
		}

		return walletTransactions.OrderByBlockchain().ToList();
	}

	/// <summary>
	/// Get all wallet transactions along with corresponding amounts ordered by blockchain.
	/// </summary>
	/// <param name="sortForUi"><c>true</c> to sort by "first seen", "height", and "block index", <c>false</c> to sort by "height", "block index", and "first seen".</param>
	/// <remarks>Transaction amount specifies how it affected your final wallet balance (spend some bitcoin, received some bitcoin, or no change).</remarks>
	public async Task<List<TransactionSummary>> BuildHistorySummaryAsync(bool sortForUi = false, CancellationToken cancellationToken = default)
	{
		var cpfpInfos = await CpfpInfoProvider.GetCachedCpfpInfoAsync(cancellationToken).ConfigureAwait(false);

		Dictionary<uint256, TransactionSummary> mapByTxid = new();

		foreach (SmartCoin coin in GetAllCoins())
		{
			if (mapByTxid.TryGetValue(coin.TransactionId, out TransactionSummary? found)) // If found then update.
			{
				found.Amount += coin.Amount;
			}
			else
			{
				FeeRate? effectiveFeeRate = null;
				if (cpfpInfos.FirstOrDefault(x => x.Transaction == coin.Transaction) is { } cachedCpfpInfo)
				{
					effectiveFeeRate = cachedCpfpInfo.CpfpInfo.EffectiveFeeRate;
				}

				mapByTxid.Add(coin.TransactionId, new TransactionSummary(coin.Transaction, coin.Amount, effectiveFeeRate));
			}

			if (coin.SpenderTransaction is { } spenderTransaction)
			{
				var spenderTxId = spenderTransaction.GetHash();

				if (mapByTxid.TryGetValue(spenderTxId, out TransactionSummary? foundSpenderCoin)) // If found then update.
				{
					foundSpenderCoin.Amount -= coin.Amount;
				}
				else
				{
					FeeRate? effectiveFeeRate = null;
					if (cpfpInfos.FirstOrDefault(x => x.Transaction == coin.Transaction) is { } cachedCpfpInfo)
					{
						effectiveFeeRate = cachedCpfpInfo.CpfpInfo.EffectiveFeeRate;
					}

					mapByTxid.Add(spenderTxId, new TransactionSummary(spenderTransaction, Money.Zero - coin.Amount, effectiveFeeRate));
				}
			}
		}

		return sortForUi
			? mapByTxid.Values.OrderBy(x => x.FirstSeen).ThenBy(x => x.Height).ThenBy(x => x.BlockIndex).ToList()
			: mapByTxid.Values.OrderByBlockchain().ToList();
	}

	public HdPubKey GetNextReceiveAddress()
	{
		return KeyManager.GetNextReceiveKey();
	}

	public int GetPrivacyPercentage()
	{
		var currentPrivacyScore = Coins.Sum(x => x.Amount.Satoshi * Math.Min(x.AnonymitySet - 1, x.IsPrivate(AnonScoreTarget) ? AnonScoreTarget - 1 : AnonScoreTarget - 2));
		var maxPrivacyScore = Coins.TotalAmount().Satoshi * (AnonScoreTarget - 1);
		int pcPrivate = maxPrivacyScore == 0M ? 0 : (int)(currentPrivacyScore * 100 / maxPrivacyScore);

		return pcPrivate;
	}

	public bool TryLogin(string password)
	{
		if (PasswordHelper.TryPassword(KeyManager, password))
		{
			IsLoggedIn = true;
			Password = Guard.Correct(password);
			KeyChain = new KeyChain(KeyManager, Password);
		}

		return IsLoggedIn;
	}

	public void Logout()
	{
		IsLoggedIn = false;
	}

	/// <inheritdoc/>
	public override async Task StartAsync(CancellationToken cancellationToken)
	{
		LoadWalletState();
		Logger.LogTrace(FormatLog("State is loaded.", this));

		// The blocks that the wallet did not see yet come from the node. The unconfirmed transactions
		// that the wallet remembers are loaded when that is done, see the synchronizer.
		await Synchronizer.StartAsync(cancellationToken).ConfigureAwait(false);
		Logger.LogTrace(FormatLog("Waiting for the synchronization with the node to finish.", this));
		await Synchronizer.InitialSynchronizationFinished.WaitAsync(cancellationToken).ConfigureAwait(false);

		LoadExcludedCoins();

		await base.StartAsync(cancellationToken).ConfigureAwait(false);

		Loaded = true;
		_eventBus.Publish(new WalletLoaded(this));
	}

	private void LoadExcludedCoins()
	{
		bool isUpdateRequired = false;
		foreach (var excludedCoin in KeyManager.ExcludedCoinsFromCoinJoin)
		{
			var coin = Coins.SingleOrDefault(c => c.Outpoint == excludedCoin);
			if (coin != null)
			{
				coin.IsExcludedFromCoinJoin = true;
			}
			else
			{
				isUpdateRequired = true;
			}
		}
		if (isUpdateRequired)
		{
			UpdateExcludedCoinFromCoinJoin();
		}
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		Logger.LogInfo(FormatLog("is fully synchronized.", this));
	}

	public string AddCoinJoinPayment(IDestination destination, Money amount)
	{
		var paymentId = BatchedPayments.AddPayment(destination, amount);
		_eventBus.Publish(new PaymentBatchChanged(BatchedPayments.GetPayments()));
		return paymentId.ToString();
	}

	public void CancelCoinJoinPayment(Guid paymentId)
	{
		BatchedPayments.AbortPayment(paymentId);
		_eventBus.Publish(new PaymentBatchChanged(BatchedPayments.GetPayments()));
	}

	public void AddCoinjoinCosts(uint256 transactionId, CoinjoinCosts coinjoinCosts)
	{
		KeyManager.AddCoinjoinCosts(transactionId, coinjoinCosts);
		_eventBus.Publish(new CoinjoinCostsAdded(transactionId, coinjoinCosts));
	}

	/// <inheritdoc/>
	public override async Task StopAsync(CancellationToken cancel)
	{
		await base.StopAsync(cancel).ConfigureAwait(false);
		await Synchronizer.StopAsync(cancel).ConfigureAwait(false);
		Synchronizer.Dispose();

		_disposables.Dispose();
	}

	private void WalletRelevantTransactionProcessed(ProcessedResult e)
	{
		try
		{
			if (e.Transaction.CanBeSpeedUpUsingCpfp())
			{
				CpfpInfoProvider.ScheduleRequest(e.Transaction);
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(FormatLog(ex.ToString(), this));
		}

		try
		{
			if (BatchedPayments.TryResolvePaymentsWithTransaction(e.Transaction))
			{
				_eventBus.Publish(new PaymentBatchChanged(BatchedPayments.GetPayments()));
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(FormatLog(ex.ToString(), this));
		}
	}

	private void Mempool_TransactionReceived(SmartTransaction tx)
	{
		try
		{
			if (!TransactionProcessor.IsAware(tx.GetHash()))
			{
				TransactionProcessor.Process(tx);
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(FormatLog(ex.ToString(), this));
		}
	}

	/// <summary>
	/// Loads what the wallet knew about the chain when it ran the last time.
	/// </summary>
	private void LoadWalletState()
	{
		// Make sure that the keys are asserted in case of an empty HdPubKeys array.
		KeyManager.GetKeys();

		TransactionProcessor.Process(TransactionStore.ConfirmedStore.GetTransactions());
	}

	/// <summary>
	/// Loads the unconfirmed transactions that the wallet remembers. This is done once the wallet processed every block,
	/// which tells the transactions that got confirmed in the meantime from those that are still waiting.
	/// </summary>
	private void LoadDummyMempool()
	{
		if (TransactionStore.MempoolStore.IsEmpty())
		{
			return;
		}

		var txsToProcess = new List<SmartTransaction>();
		foreach (var tx in TransactionStore.MempoolStore.GetTransactions())
		{
			var txid = tx.GetHash();
			if (DateTimeOffset.UtcNow - tx.FirstSeen < TimeSpan.FromDays(ServiceConfiguration.DropUnconfirmedTransactionsAfterDays))
			{
				txsToProcess.Add(tx);
			}
			else
			{
				if (TransactionStore.MempoolStore.TryRemove(txid, out _))
				{
					Logger.LogInfo(FormatLog($"Transaction {txid} dropped after {ServiceConfiguration.DropUnconfirmedTransactionsAfterDays} days being unconfirmed.", this));
				}
			}
		}

		TransactionProcessor.Process(txsToProcess);
	}

	public void ExcludeCoinFromCoinJoin(OutPoint outpoint, bool exclude = true)
	{
		if (!Coins.TryGetByOutPoint(outpoint, out var coin))
		{
			throw new InvalidOperationException($"Coin '{outpoint}' doesn't belong to the wallet or is spent.");
		}

		coin.IsExcludedFromCoinJoin = exclude;
		UpdateExcludedCoinFromCoinJoin();
	}

	public void UpdateExcludedCoinsFromCoinJoin(OutPoint[] outPointsToExclude)
	{
		foreach (var coin in Coins)
		{
			coin.IsExcludedFromCoinJoin = outPointsToExclude.Contains(coin.Outpoint);
		}

		UpdateExcludedCoinFromCoinJoin();
	}

	private void UpdateExcludedCoinFromCoinJoin()
	{
		var excludedOutpoints = Coins.Where(c => c.IsExcludedFromCoinJoin).Select(c => c.Outpoint);
		KeyManager.SetExcludedCoinsFromCoinJoin(excludedOutpoints);
	}
}
