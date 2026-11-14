using System;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Helpers;
using WalletWasabi.Services;

namespace WalletWasabi.Client;

public class StatusContainer : IDisposable
{
	public bool IsTorRunning { get; private set; }
	public FeeRateEstimations? FeeRates { get; private set; }
	/// <summary>Height of the chain, as the node knows it from the headers. Zero as long as the node did not tell.</summary>
	public uint BestNetworkHeight { get; private set; }

	/// <summary>What the node answered the last time it was asked about itself, or why it did not. Null before the first answer.</summary>
	public Result<ConnectedRpcStatus, string>? BitcoinRpcStatus { get; private set; }

	private readonly IDisposable _torConnectionSubscription;
	private readonly IDisposable _feeRateSubscription;
	private readonly IDisposable _bestHeightSubscription;
	private readonly IDisposable _rpcStatusSubscription;

	public StatusContainer(EventBus eventBus)
	{
		_torConnectionSubscription =
			eventBus.Subscribe<TorConnectionStateChanged>(e => IsTorRunning = e.IsTorRunning);

		_feeRateSubscription =
			eventBus.Subscribe<MiningFeeRatesChanged>(e => FeeRates = e.AllFeeEstimate);

		_bestHeightSubscription =
			eventBus.Subscribe<NetworkTipHeightChanged>(e => BestNetworkHeight = e.Height);

		_rpcStatusSubscription =
			eventBus.Subscribe<RpcStatusChanged>(e => BitcoinRpcStatus = e.Status);
	}

	public void Dispose()
	{
		_torConnectionSubscription.Dispose();
		_feeRateSubscription.Dispose();
		_bestHeightSubscription.Dispose();
		_rpcStatusSubscription.Dispose();
	}
}
