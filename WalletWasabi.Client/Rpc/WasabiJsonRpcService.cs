using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Rpc;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;
using WalletWasabi.Wallets.Slip39;
using JsonRpcResult = System.Collections.Generic.Dictionary<string, object?>;
using JsonRpcResultList = System.Collections.Immutable.ImmutableArray<System.Collections.Generic.Dictionary<string, object?>>;

namespace WalletWasabi.Client.Rpc;

public class WasabiJsonRpcService : IJsonRpcService
{
	public WasabiJsonRpcService(Global global)
	{
		Global = global;
	}

	private const string NoWalletMessage = "There is no wallet. Create one with 'createwallet' or recover one with 'recoverwallet'.";

	private Global Global { get; }

	/// <summary>The wallet of the application. It is set before every method that works with the wallet.</summary>
	private Wallet? ActiveWallet { get; set; }

	[JsonRpcMethod("listunspentcoins")]
	public JsonRpcResultList GetUnspentCoinList()
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		AssertWalletIsLoaded();
		var tipHeight = GetTipHeight(activeWallet);
		return activeWallet.Coins.Where(x => !x.IsSpent()).Select(
			x => new JsonRpcResult
			{
				["txid"] = x.TransactionId.ToString(),
				["index"] = x.Index,
				["amount"] = x.Amount.Satoshi,
				["anonymityScore"] = x.AnonymitySet,
				["confirmed"] = x.Confirmed,
				["confirmations"] = x.Transaction.GetConfirmations(tipHeight),
				["keyPath"] = x.HdPubKey.FullKeyPath.ToString(),
				["address"] = x.HdPubKey.GetAddress(Global.Network).ToString(),
				["excludedFromCoinjoin"] = x.IsExcludedFromCoinJoin
			}).ToImmutableArray();
	}

	[JsonRpcMethod("listcoins")]
	public JsonRpcResultList GetCoinList()
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		AssertWalletIsLoaded();
		var tipHeight = GetTipHeight(activeWallet);
		if (activeWallet.Coins is not { } coinRegistry)
		{
			throw new ArgumentException($"{nameof(activeWallet.Coins)} was not {typeof(CoinsRegistry)}.");
		}
		return coinRegistry.AsAllCoinsView().Select(
			x => new JsonRpcResult
			{
				["txid"] = x.TransactionId.ToString(),
				["index"] = x.Index,
				["amount"] = x.Amount.Satoshi,
				["anonymityScore"] = x.AnonymitySet,
				["confirmed"] = x.Confirmed,
				["confirmations"] = x.Transaction.GetConfirmations(tipHeight),
				["keyPath"] = x.HdPubKey.FullKeyPath.ToString(),
				["address"] = x.HdPubKey.GetAddress(Global.Network).ToString(),
				["spentBy"] = x.SpenderTransaction?.GetHash().ToString()
			}).ToImmutableArray();
	}

	/// <summary>
	/// Creates the wallet and returns its SLIP-39 backup: <paramref name="shares"/> shares, any <paramref name="threshold"/> of which recover the wallet.
	/// </summary>
	/// <param name="password">Encrypts the wallet secret in the wallet file. It is not part of the backup.</param>
	/// <remarks>There is a single wallet: the call fails if it exists already.</remarks>
	[JsonRpcMethod("createwallet", initializable: false)]
	public object CreateWallet(string password, byte threshold = WalletGenerator.DefaultShamirThreshold, byte shares = WalletGenerator.DefaultShamirShares)
	{
		Global.WalletManager.AssertWalletCanBeAdded();

		// Nothing concerns a new wallet up to the tip of the chain. If the node did not tell it yet, the wallet learns it when it reaches the node.
		var walletGenerator = new WalletGenerator(Global.Network)
		{
			TipHeight = Global.Status.BestNetworkHeight > 0 ? new Height.ChainHeight(Global.Status.BestNetworkHeight) : null
		};
		var backup = WalletGenerator.GenerateShares(threshold, shares);
		var keyManager = walletGenerator.GenerateWallet(password, backup.Take(threshold).ToArray());
		Global.WalletManager.AddWallet(keyManager);
		return backup.Select(share => share.ToMnemonic(WordList.Wordlist)).ToArray();
	}

	/// <summary>
	/// Recovers the wallet from the SLIP-39 shares of its backup.
	/// </summary>
	/// <param name="shares">As many shares as the threshold of the backup requires.</param>
	/// <param name="password">Encrypts the wallet secret in the wallet file. It does not have to be the one the wallet was created with.</param>
	/// <param name="birthHeight">
	/// Height of the first block to look for the transactions of the wallet in. Every block from there on is fetched from the node,
	/// so a height shortly before the first transaction of the wallet saves time. By default the block that Taproot activated at.
	/// </param>
	/// <remarks>There is a single wallet: the call fails if it exists already.</remarks>
	[JsonRpcMethod("recoverwallet", initializable: false)]
	public void RecoverWallet(string[] shares, string password = "", uint? birthHeight = null)
	{
		Global.WalletManager.AssertWalletCanBeAdded();

		if (!TryParseShares(shares, out var parsedShares))
		{
			throw new ArgumentException("Invalid value for shares");
		}

		// The wallet must open with exactly the entered password.
		PasswordHelper.Guard(password);

		var keyManager = KeyManager.Recover(
			parsedShares,
			password,
			Global.Network,
			birthHeight: birthHeight is { } height ? new Height.ChainHeight(height) : null);
		Global.WalletManager.AddWallet(keyManager);
	}

	/// <summary>
	/// Starts synchronizing the wallet, unless it is running already.
	/// </summary>
	[JsonRpcMethod("loadwallet", initializable: false)]
	public void LoadWallet()
	{
		var wallet = Global.WalletManager.Wallet ?? throw new InvalidOperationException(NoWalletMessage);

		ActiveWallet = wallet;
		if (!wallet.Loaded)
		{
			Global.WalletManager.StartWalletAsync(wallet).ConfigureAwait(false);
		}
	}

	[JsonRpcMethod("getwalletinfo")]
	public JsonRpcResult WalletInfo()
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		var km = activeWallet.KeyManager;
		var info = new JsonRpcResult
		{
			["walletFile"] = km.FilePath,
			["loaded"] = activeWallet.Loaded,
			["masterKeyFingerprint"] = km.MasterFingerprint?.ToString() ?? "",
			["anonScoreTarget"] = activeWallet.AnonScoreTarget,
			["isAutoCoinjoin"] = activeWallet.KeyManager.AutoCoinJoin,
			["isNonPrivateCoinIsolation"] = activeWallet.KeyManager.NonPrivateCoinIsolation,
			["onlyUsePrivateFundsForPayments"] = activeWallet.KeyManager.OnlyUsePrivateFundsForPayments,
			["accounts"] = new[]
			{
				new JsonRpcResult
				{
					["name"] = "taproot",
					["publicKey"] = km.TaprootExtPubKey.ToString(Global.Network),
					["keyPath"] = $"m/{km.TaprootAccountKeyPath}"
				}
			}
		};

		if (activeWallet.Loaded)
		{
			// The following elements are valid only after the wallet is fully synchronized
			info["balance"] = activeWallet.Coins
				.Where(c => !c.IsSpent())
				.Sum(c => c.Amount.Satoshi);
			info["coinjoinStatus"] = GetCoinjoinStatus(activeWallet);
		}

		return info;
	}

	/// <summary>
	/// Gives out a receive address that was not given out before.
	/// </summary>
	[JsonRpcMethod("getnewaddress")]
	public JsonRpcResult GenerateReceiveAddress()
	{
		AssertWalletIsLoaded();
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		var hdKey = activeWallet.KeyManager.GetNextReceiveKey();

		return new JsonRpcResult
		{
			["address"] = hdKey.GetAddress(Global.Network).ToString(),
			["keyPath"] = hdKey.FullKeyPath.ToString(),
			["publicKey"] = hdKey.PubKey.ToHex(),
			["scriptPubKey"] = hdKey.GetAssumedScriptPubKey().ToHex()
		};
	}

	[JsonRpcMethod("getstatus", initializable: false)]
	public JsonRpcResult GetStatus()
	{
		// Everything about the chain comes from the user's node.
		var nodeStatus = Global.Status.BitcoinRpcStatus;
		var node = nodeStatus is { IsOk: true } ? nodeStatus.Value : null;

		var status = new JsonRpcResult
		{
			["torStatus"] = (Global.Config.UseTor, Global.Status.IsTorRunning) switch
			{
				(TorMode.Disabled, _) => "Turned off",
				(_, true) => "Running",
				(_, false) => "Not running"
			},
			["onionService"] = Global.OnionServiceUri?.ToString() ?? "Unavailable",
			["network"] = Global.Network.Name,
			["bitcoinNode"] = nodeStatus is null
				? "Not asked yet"
				: nodeStatus.Match(s => s.ToString() ?? "", e => e),
			["bestBlockchainHeight"] = node?.Blocks.ToString() ?? "",
			["bestBlockchainHash"] = node?.BestBlockHash.ToString() ?? "",
		};

		if (Global.WalletManager.Wallet is { } wallet)
		{
			status["walletHeight"] = wallet.KeyManager.GetBestHeight()?.Height.ToString() ?? "";
			status["walletSynchronization"] = wallet.Synchronizer.Status.ToUserFriendlyString();
		}

		return status;
	}

	/// <summary>
	/// The height that the confirmations of the wallet's transactions are counted to: the last block that the wallet processed.
	/// </summary>
	private static uint GetTipHeight(Wallet wallet) =>
		wallet.KeyManager.GetBestHeight()?.Height ?? 0;

	[JsonRpcMethod("build")]
	public string BuildTransaction(PaymentInfo[] payments, OutPoint[] coins, int? feeTarget = null, decimal? feeRate = null, string? password = null)
	{
		Guard.NotNull(nameof(payments), payments);
		Guard.NotNull(nameof(coins), coins);
		password = Guard.Correct(password);

		var feeStrategy = GetFeeStrategy(feeTarget, feeRate);

		AssertWalletIsLoaded();
		var payment = new PaymentIntent(
			payments.Select(
				p =>
				new DestinationRequest(p.Sendto, MoneyRequest.Create(p.Amount, p.SubtractFee))));
		var result = ActiveWallet!.BuildTransaction(
			password,
			payment,
			feeStrategy,
			allowUnconfirmed: true,
			allowedInputs: coins);
		var smartTx = result.Transaction;

		return smartTx.Transaction.ToHex();
	}

	/// <summary>
	/// Unsafe, because no matter how big fee the user chooses, the wallet will build the transaction.
	/// Potentially, the user can burn his money using this method, so be careful!
	/// </summary>
	[JsonRpcMethod("buildunsafetransaction")]
	public string BuildUnsafeTransaction(PaymentInfo[] payments, OutPoint[] coins, int? feeTarget = null, decimal? feeRate = null, string? password = null)
	{
		Guard.NotNull(nameof(payments), payments);
		Guard.NotNull(nameof(coins), coins);
		password = Guard.Correct(password);

		var feeStrategy = GetFeeStrategy(feeTarget, feeRate);

		AssertWalletIsLoaded();
		var payment = new PaymentIntent(
			payments.Select(
				p =>
				new DestinationRequest(p.Sendto, MoneyRequest.Create(p.Amount, p.SubtractFee))));
		var result = ActiveWallet!.BuildTransactionWithoutOverpaymentProtection(
			password,
			payment,
			feeStrategy,
			allowUnconfirmed: true,
			allowedInputs: coins);
		var smartTx = result.Transaction;

		return smartTx.Transaction.ToHex();
	}

	[JsonRpcMethod("payincoinjoin")]
	public string PayInCoinJoin(BitcoinAddress address, Money amount, string? password = null)
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);
		AssertWalletIsLoaded();
		AssertWalletIsLoggedIn(activeWallet, password ?? "");
		return activeWallet.AddCoinJoinPayment(address, amount);
	}

	[JsonRpcMethod("listpaymentsincoinjoin")]
	public JsonRpcResultList ListPaymentsInCoinJoin()
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);
		AssertWalletIsLoaded();
		var payments = activeWallet.BatchedPayments.GetPayments();
		return payments.Select(x =>
		{
			var paymentResult = new JsonRpcResult
			{
				["id"] = x.Id,
				["amount"] = x.Amount.Satoshi,
				["destination"] = x.Destination.ScriptPubKey.ToHex()
			};

			var state = x.State;
			var stateHistory = new List<JsonRpcResult>();
			while (state != null)
			{
				switch (state)
				{
					case PendingPayment pending:
						stateHistory.Add(new JsonRpcResult
						{
							["status"] = "Pending"
						});
						break;

					case InProgressPayment inProgress:
						stateHistory.Add(new JsonRpcResult
						{
							["status"] = "In progress",
							["round"] = inProgress.RoundId.ToString()
						});
						break;

					case SignedUnknownPayment signed:
						stateHistory.Add(new JsonRpcResult
						{
							["status"] = "Signed",
							["txid"] = signed.TransactionId.ToString()
						});
						break;

					case FinishedPayment finished:
						stateHistory.Add(new JsonRpcResult
						{
							["status"] = "Finished",
							["txid"] = finished.TransactionId.ToString()
						});
						break;

					default:
						throw new NotSupportedException($"Unrecognized state: {state.GetType().Name}.");
				}

				state = state.PreviousState;
			}

			paymentResult["state"] = stateHistory;

			if (x.Destination.ScriptPubKey.GetDestinationAddress(activeWallet.Network) is { } address)
			{
				paymentResult["address"] = address;
			}
			return paymentResult;
		}).ToImmutableArray();
	}

	[JsonRpcMethod("cancelpaymentincoinjoin")]
	public void CancelPayment(Guid paymentId)
	{
		AssertWalletIsLoaded();
		ActiveWallet!.CancelCoinJoinPayment(paymentId);
	}

	[JsonRpcMethod("send")]
	public async Task<JsonRpcResult> SendTransactionAsync(PaymentInfo[] payments, OutPoint[] coins, int? feeTarget = null, decimal? feeRate = null, string? password = null)
	{
		password = Guard.Correct(password);
		var txHex = BuildTransaction(payments, coins, feeTarget, feeRate, password);
		var smartTx = new SmartTransaction(Transaction.Parse(txHex, Global.Network), Height.Mempool);

		await Global.TransactionBroadcaster.SendTransactionAsync(smartTx).ConfigureAwait(false);
		return new JsonRpcResult
		{
			["txid"] = smartTx.Transaction.GetHash(),
			["tx"] = txHex
		};
	}

	[JsonRpcMethod("canceltransaction")]
	public string BuildCancelTransaction(uint256 txId, string password = "")
	{
		Guard.NotNull(nameof(txId), txId);
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);
		AssertWalletIsLoaded();
		AssertWalletIsLoggedIn(activeWallet, password);
		var mempoolStore = Global.TransactionStore.MempoolStore;
		if (!mempoolStore.TryGetTransaction(txId, out var smartTransactionToCancel))
		{
			throw new NotSupportedException($"Unknown transaction {txId}");
		}

		var cancellationResult = activeWallet.CancelTransaction(smartTransactionToCancel);
		var cancellationSmartTransaction = cancellationResult.Transaction;
		return cancellationSmartTransaction.Transaction.ToHex();
	}

	[JsonRpcMethod("speeduptransaction")]
	public async Task<string> SpeedUpTransactionAsync(uint256 txId, string password = "")
	{
		Guard.NotNull(nameof(txId), txId);
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);
		AssertWalletIsLoaded();
		AssertWalletIsLoggedIn(activeWallet, password);
		var mempoolStore = Global.TransactionStore.MempoolStore;
		if (!mempoolStore.TryGetTransaction(txId, out var smartTransactionToSpeedUp))
		{
			throw new NotSupportedException($"Unknown transaction {txId}");
		}

		var speedUpResult = await activeWallet.SpeedUpTransactionAsync(smartTransactionToSpeedUp, null, CancellationToken.None).ConfigureAwait(false);
		var speedUpSmartTransaction = speedUpResult.Transaction;
		return speedUpSmartTransaction.Transaction.ToHex();
	}

	[JsonRpcMethod("broadcast", initializable: false)]
	public async Task<JsonRpcResult> SendRawTransactionAsync(string txHex)
	{
		txHex = Guard.Correct(txHex);
		var smartTx = new SmartTransaction(Transaction.Parse(txHex, Global.Network), Height.Mempool);

		await Global.TransactionBroadcaster.SendTransactionAsync(smartTx).ConfigureAwait(false);
		return new JsonRpcResult
		{
			["txid"] = smartTx.Transaction.GetHash()
		};
	}

	[JsonRpcMethod("gethistory")]
	public async Task<JsonRpcResultList> GetHistoryAsync()
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		AssertWalletIsLoaded();
		var summary = await activeWallet.BuildHistorySummaryAsync();
		return summary.Select(
			x => new JsonRpcResult
			{
				["datetime"] = x.FirstSeen,
				["height"] =  x.Height.ToString(),
				["amount"] = x.Amount.Satoshi,
				["tx"] = x.GetHash(),
				["islikelycoinjoin"] = x.IsOwnCoinjoin()
			}).ToImmutableArray();
	}

	[JsonRpcMethod("excludefromcoinjoin")]
	public void ExcludeCoinsFromCoinjoin(uint256 transactionId, int n, bool exclude = true)
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		AssertWalletIsLoaded();

		activeWallet.ExcludeCoinFromCoinJoin(new OutPoint(transactionId, n), exclude);
	}

	[JsonRpcMethod("listkeys")]
	public JsonRpcResultList GetAllKeys()
	{
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		AssertWalletIsLoaded();
		var keys = activeWallet.KeyManager.GetKeys();
		return keys.Select(
			x => new JsonRpcResult
			{
				["fullKeyPath"] = x.FullKeyPath.ToString(),
				["internal"] = x.IsInternal,
				["keyState"] = x.KeyState,
				["handedOut"] = x.IsHandedOut,
				["scriptPubKey"] = x.GetAssumedScriptPubKey().ToString(),
				["pubkey"] = x.PubKey.ToString(),
				["address"] = x.GetAddress(Global.Network).ToString()
			}).ToImmutableArray();
	}

	[JsonRpcMethod("startcoinjoin")]
	public void StartCoinJoining(string? password = null, bool stopWhenAllMixed = true, bool overridePlebStop = true)
	{
		var coinJoinManager = GetCoinJoinManager();
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		AssertWalletIsLoaded();
		AssertWalletIsLoggedIn(activeWallet, password ?? "");
		coinJoinManager.RequestCoinJoinStart(activeWallet, stopWhenAllMixed, overridePlebStop);
	}

	[JsonRpcMethod("stopcoinjoin")]
	public void StopCoinJoining()
	{
		var coinJoinManager = GetCoinJoinManager();
		var activeWallet = Guard.NotNull(nameof(ActiveWallet), ActiveWallet);

		AssertWalletIsLoaded();

		coinJoinManager.RequestCoinJoinStop(activeWallet);
	}

	[JsonRpcMethod("getfeerates", initializable: false)]
	public object GetFeeRate()
	{
		if (Global.Status.FeeRates is { } nonNullFeeRates)
		{
			return nonNullFeeRates.Estimations;
		}

		return new Dictionary<int, int>();
	}

	[JsonRpcMethod(IJsonRpcService.StopRpcCommand, initializable: false)]
	public Task StopAsync()
	{
		throw new InvalidOperationException("This RPC method is special and the handling method should not be called.");
	}

	private CoinJoinManager GetCoinJoinManager()
	{
		var coinJoinManager = Global.HostedServices.GetOrDefault<CoinJoinManager>()
			?? throw new InvalidOperationException("No coordinator configured.");

		return coinJoinManager;
	}

	private string GetCoinjoinStatus(Wallet wallet)
	{
		var coinJoinManager = GetCoinJoinManager();
		var walletCoinjoinClientState = coinJoinManager.GetCoinjoinClientState(wallet.WalletId);
		return walletCoinjoinClientState switch
		{
			CoinJoinClientState.Idle => "Idle",
			CoinJoinClientState.InProgress => "In progress",
			CoinJoinClientState.InSchedule => "In schedule",
			CoinJoinClientState.InCriticalPhase => "In critical phase",
			_ => throw new Exception($"The state {walletCoinjoinClientState.FriendlyName()} is unknown.")
		};
	}

	private void AssertWalletIsLoaded()
	{
		if (ActiveWallet is not {Loaded: true})
		{
			throw new InvalidOperationException("There is no wallet loaded.");
		}
	}

	private void AssertWalletIsLoggedIn(Wallet activeWallet, string password)
	{
		if (!activeWallet.IsLoggedIn && !activeWallet.TryLogin(password))
		{
			throw new Exception("The wallet requires the password to start coinjoining.");
		}
	}

	/// <summary>
	/// Called before every method that works with the wallet.
	/// </summary>
	/// <param name="path">Path of the request. The wallet is not addressed by a name, so it must be the root.</param>
	[JsonRpcInitialization]
	public void Initialize(string path, bool needsWallet)
	{
		if (!string.IsNullOrEmpty(path.Trim('/')))
		{
			throw new InvalidOperationException("There is a single wallet and it has no name. Send the request to the root path.");
		}

		ActiveWallet = Global.WalletManager.Wallet ?? throw new InvalidOperationException(NoWalletMessage);
	}

	private static bool TryParseShares(string[] mnemonics, [NotNullWhen(true)] out Share[]? shares)
	{
		try
		{
			shares = mnemonics.Select(mnemonic => Share.FromMnemonic(mnemonic.Trim().ToLowerInvariant())).ToArray();
			return shares.Length > 0;
		}
		catch (Exception)
		{
			shares = null;
			return false;
		}
	}

	private FeeStrategy GetFeeStrategy(int? feeTarget = null, decimal? feeRate = null)
	{
		static bool InRange<T>(IComparable<T> val, T min, T max) =>
			val.CompareTo(min) >= 0 && val.CompareTo(max) <= 0;

		var satsPerByte = feeRate is { } nonNullSatsPerByte ? new FeeRate(nonNullSatsPerByte) : FeeRate.Zero;

		return (feeRate, feeTarget) switch
		{
			(not null, null) when InRange(satsPerByte, Constants.MinRelayFeeRate, Constants.AbsurdlyHighFeeRate) =>
				FeeStrategy.CreateFromFeeRate(satsPerByte),
			(null, { } argFeeTarget) when InRange(argFeeTarget, Constants.TwentyMinutesConfirmationTarget, Constants.SevenDaysConfirmationTarget) =>
				FeeStrategy.CreateFromConfirmationTarget(argFeeTarget),
			_ => throw new ArgumentException("Fee parameters are missing, inconsistent or out of range.")
		};
	}
}
