using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;

namespace WalletWasabi.WabiSabi.Client;

public class CoinJoinTrackerFactory
{
	public CoinJoinTrackerFactory(
		Func<string, IWabiSabiApiRequestHandler> arenaRequestHandlerFactory,
		RoundStateProvider roundStatusProvider,
		CoinJoinConfiguration coinJoinConfiguration,
		InputVerifier inputVerifier,
		CancellationToken cancellationToken)
	{
		ArenaRequestHandlerFactory = arenaRequestHandlerFactory;
		_roundStatusProvider = roundStatusProvider;
		_coinJoinConfiguration = coinJoinConfiguration;
		_inputVerifier = inputVerifier;
		_cancellationToken = cancellationToken;
		_liquidityClueProvider = new LiquidityClueProvider();
	}

	private Func<string, IWabiSabiApiRequestHandler> ArenaRequestHandlerFactory { get; }
	private readonly RoundStateProvider _roundStatusProvider;
	private readonly CoinJoinConfiguration _coinJoinConfiguration;
	private readonly InputVerifier _inputVerifier;
	private readonly CancellationToken _cancellationToken;
	private readonly LiquidityClueProvider _liquidityClueProvider;

	public CoinJoinTracker CreateAndStart(Wallet wallet, Func<IEnumerable<SmartCoin>> coinCandidatesFunc)
	{
		_liquidityClueProvider.InitLiquidityClue(wallet);

		if (wallet.KeyChain is null)
		{
			throw new NotSupportedException("Wallet has no key chain.");
		}

		var coinJoinCoinSelector = CoinJoinCoinSelector.FromWallet(wallet);
		var coinSelector = CoinSelectors.ComposedCoinSelector([
			CoinSelectors.PrivatePaymentCoinSelector(wallet.BatchedPayments, coinJoinCoinSelector.SemiPrivateThreshold),
			coinJoinCoinSelector.SelectCoinsForRound]);
		var coinJoinClient = new CoinJoinClient(
			ArenaRequestHandlerFactory,
			wallet.KeyChain,
			wallet.OutputProvider,
			_roundStatusProvider,
			coinSelector,
			_coinJoinConfiguration,
			_inputVerifier,
			_liquidityClueProvider,
			doNotRegisterInLastMinuteTimeLimit: TimeSpan.FromMinutes(1));

		return new CoinJoinTracker(wallet, coinJoinClient, coinCandidatesFunc, _cancellationToken);
	}
}
