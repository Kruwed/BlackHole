using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Services;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class HealthMonitor : ReactiveObject
{
	[AutoNotify] private decimal _priorityFee;
	[AutoNotify] private uint _blockchainTip;
	[AutoNotify] private uint _clientTip;
	[AutoNotify] private TorStatus _torStatus;
	[AutoNotify] private Result<ConnectedRpcStatus, string> _bitcoinRpcStatus;
	[AutoNotify] private HealthMonitorState _state;

	/// <summary>What is told about the node until it answers, or fails to, for the first time.</summary>
	private const string BitcoinRpcNotAskedYet = "is being contacted...";

	public HealthMonitor(IServices services)
	{
		// Do not make it dynamic, because if you change this config settings only next time will it activate.
		UseTor = services.GetUseTor();
		TorStatus = UseTor == TorMode.Disabled ? TorStatus.TurnedOff : TorStatus.NotRunning;

		_bitcoinRpcStatus = Result<ConnectedRpcStatus, string>.Fail(BitcoinRpcNotAskedYet);

		// Priority Fee
		services.EventBus.AsObservable<MiningFeeRatesChanged>()
			.Select(e => e.AllFeeEstimate.Estimations.FirstOrDefault(x => x.Key == 2).Value)
			.WhereNotNull()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(priorityFee => PriorityFee = priorityFee.SatoshiPerByte);

		// Blockchain Tip: the height of the chain, as the node tells it.
		services.EventBus.AsObservable<NetworkTipHeightChanged>()
			.Select(value => value.Height)
			.WhereNotNull()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(blockchainTip => BlockchainTip = blockchainTip);

		// Local Tip: the last block that the wallet processed.
		services.EventBus.AsObservable<ClientTipHeightChanged>()
			.Select(value => value.Height)
			.WhereNotNull()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(clientTip => ClientTip = clientTip);

		// Tor Status
		services.EventBus.AsObservable<TorConnectionStateChanged>()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Select(status => (UseTor, status.IsTorRunning) switch
			{
				(TorMode.Disabled, _) => TorStatus.TurnedOff,
				(_, true) => TorStatus.Running,
				(_, false) => TorStatus.NotRunning
			})
			.BindTo(this, x => x.TorStatus)
			.DisposeWith(Disposables);

		// Bitcoin Core Status
		services.EventBus.AsObservable<RpcStatusChanged>()
			.Select(x => x.Status)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(x => BitcoinRpcStatus = x)
			.DisposeWith(Disposables);

		// State
		this.WhenAnyValue(
				x => x.TorStatus,
				x => x.BlockchainTip,
				x => x.ClientTip,
				x => x.BitcoinRpcStatus)
			.Throttle(TimeSpan.FromMilliseconds(100))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Select(_ => GetState())
			.BindTo(this, x => x.State)
			.DisposeWith(Disposables);
	}

	public TorMode UseTor { get; }

	private CompositeDisposable Disposables { get; } = new();

	public void Dispose()
	{
		Disposables.Dispose();
	}

	private HealthMonitorState GetState()
	{
		var torConnected = UseTor == TorMode.Disabled || TorStatus == TorStatus.Running;

		return _bitcoinRpcStatus.Match(
			node =>
			{
				if (!torConnected)
				{
					return HealthMonitorState.Loading;
				}

				if (!node.Synchronized)
				{
					return HealthMonitorState.BitcoinRpcSynchronizing;
				}

				// A wallet that is behind the node is still processing blocks. Without a wallet there is nothing to wait for.
				return ClientTip > 0 && ClientTip < node.Blocks
					? HealthMonitorState.Loading
					: HealthMonitorState.Ready;
			},
			e => e == BitcoinRpcNotAskedYet
				? HealthMonitorState.Loading
				: HealthMonitorState.BitcoinRpcIssueDetected);
	}
}
