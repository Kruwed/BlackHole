using Microsoft.Extensions.Hosting;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.FidelityBonds;
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
		Synchronizer = new WalletSynchronizer(
			rpcClient,
			keyManager,
			TransactionStore,
			TransactionProcessor,
			eventBus,
			onSynchronizedFirst: LoadDummyMempool,
			watchMempool: watchMempool,
			fidelityBondSearchBlocks: serviceConfiguration.FidelityBondSearchBlocks);
		Coins = TransactionProcessor.Coins;
		BatchedPayments = new PaymentBatch(Coins);
		OutputProvider = new PaymentAwareOutputProvider(DestinationProvider, BatchedPayments, RandomnessProviders.Secure, () => KeyManager.MaxCoinjoinLeftover);
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

	public ICoinsView GetAllCoins() => Coins.AsAllCoinsView();

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

		TransactionSummary GetSummary(SmartTransaction transaction, SmartTransaction cpfpSource)
		{
			var txid = transaction.GetHash();

			if (!mapByTxid.TryGetValue(txid, out var summary))
			{
				var effectiveFeeRate = cpfpInfos.FirstOrDefault(x => x.Transaction == cpfpSource) is { } cachedCpfpInfo
					? cachedCpfpInfo.CpfpInfo.EffectiveFeeRate
					: null;

				summary = new TransactionSummary(transaction, Money.Zero, effectiveFeeRate);
				mapByTxid.Add(txid, summary);
			}

			return summary;
		}

		foreach (SmartCoin coin in GetAllCoins())
		{
			GetSummary(coin.Transaction, coin.Transaction).Amount += coin.Amount;

			if (coin.SpenderTransaction is { } spenderTransaction)
			{
				GetSummary(spenderTransaction, coin.Transaction).Amount -= coin.Amount;
			}
		}

		// The coins of a fidelity bond belong to the wallet too. Locking coins of the wallet and getting them back costs
		// it nothing but the mining fees, and a bond that is paid from elsewhere is money that came in.
		foreach (var bond in GetFidelityBonds())
		{
			GetSummary(bond.Transaction, bond.Transaction).AddLockedBond(bond);

			if (bond.SpenderTransaction is { } spenderTransaction)
			{
				GetSummary(spenderTransaction, bond.Transaction).AddUnlockedBond(bond);
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

	/// <summary>
	/// The fidelity bonds of the wallet, the redeemed ones included. The coins of a bond belong to the wallet, but a bond
	/// is not one of its coins: nothing spends it but the transaction that redeems it.
	/// </summary>
	public IReadOnlyList<FidelityBond> GetFidelityBonds() => TransactionProcessor.GetFidelityBonds();

	/// <summary>What the bonds of the wallet hold that were not redeemed yet. It is part of what the wallet owns.</summary>
	public Money GetFidelityBondBalance() => GetFidelityBonds().Where(bond => !bond.IsSpent).Sum(bond => bond.Amount);

	/// <summary>
	/// Builds the transaction that pays a bond whose timelock is over back to the wallet, to an address of its own.
	/// </summary>
	/// <param name="chainHeight">The height of the last block of the chain, which decides whether the timelock is over.</param>
	/// <exception cref="InvalidOperationException">The bond cannot be redeemed, at least not now or not at this fee rate.</exception>
	/// <exception cref="System.Security.SecurityException">The password is wrong.</exception>
	public SmartTransaction BuildFidelityBondRedemption(FidelityBond bond, string password, FeeRate feeRate, uint chainHeight)
	{
		using var key = KeyManager.GetFidelityBondSecret(password, bond.UnlockHeight);
		var destination = KeyManager.GetNextChangeKey().GetAssumedScriptPubKey();
		var transaction = BondRedemption.Create(bond, key, destination, feeRate, chainHeight, Network);

		return new SmartTransaction(transaction, Height.Unknown);
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

	/// <summary>
	/// Keeps the given coins out of coinjoins or lets them in again, all of them at once. The coins that are not named stay as they are.
	/// </summary>
	/// <param name="exclusions">For every coin that is to change, whether it is kept out.</param>
	public void ExcludeCoinsFromCoinJoin(IReadOnlyDictionary<OutPoint, bool> exclusions)
	{
		foreach (var coin in Coins)
		{
			if (exclusions.TryGetValue(coin.Outpoint, out var exclude))
			{
				coin.IsExcludedFromCoinJoin = exclude;
			}
		}

		UpdateExcludedCoinFromCoinJoin();
	}

	private void UpdateExcludedCoinFromCoinJoin()
	{
		var excludedOutpoints = Coins.Where(c => c.IsExcludedFromCoinJoin).Select(c => c.Outpoint);
		KeyManager.SetExcludedCoinsFromCoinJoin(excludedOutpoints);
	}
}
