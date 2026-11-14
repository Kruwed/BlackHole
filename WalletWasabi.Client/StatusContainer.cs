using System;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Services;

namespace WalletWasabi.Client;

public class StatusContainer : IDisposable
{
	public bool IsTorRunning { get; private set; }
	public FeeRateEstimations? FeeRates { get; private set; }
	public uint BestNetworkHeight { get; private set; }

	private readonly IDisposable _torConnectionSubscription;
	private readonly IDisposable _feeRateSubscription;
	private readonly IDisposable _bestHeightSubscription;

	public StatusContainer(EventBus eventBus)
	{
		_torConnectionSubscription =
			eventBus.Subscribe<TorConnectionStateChanged>(e => IsTorRunning = e.IsTorRunning);

		_feeRateSubscription =
			eventBus.Subscribe<MiningFeeRatesChanged>(e => FeeRates = e.AllFeeEstimate);

		_bestHeightSubscription =
			eventBus.Subscribe<NetworkTipHeightChanged>(e => BestNetworkHeight = e.Height);
	}

	public void Dispose()
	{
		_torConnectionSubscription.Dispose();
		_feeRateSubscription.Dispose();
		_bestHeightSubscription.Dispose();
	}
}
