using WalletWasabi.BitcoinRpc;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.JoinMarket;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;

namespace WalletWasabi.Services;
using SubscriptionRegistry = Dictionary<Type, List<EventBus.Subscription>>;

public class EventBus
{
	private readonly SubscriptionRegistry _subscriptions = new();
	private readonly Lock _syncObj = new();

	public IDisposable Subscribe(Type eventType, Action<object> action)
	{
		lock (_syncObj)
		{
			if (!_subscriptions.ContainsKey(eventType))
			{
				_subscriptions.Add(eventType, []);
			}

			var subscription = Subscription.Create(eventType, action, this);
			_subscriptions[eventType].Add(subscription);
			return subscription;
		}
	}

	public IDisposable Subscribe<TEvent>(Action<TEvent> action) where TEvent : notnull
		=> Subscribe(typeof(TEvent), arg => action((TEvent)arg));

	private void Unsubscribe(Subscription subscription)
	{
		lock (_syncObj)
		{
			Type type = subscription.Type;
			if (_subscriptions.TryGetValue(type, out var allSubscriptions))
			{
				var subscriptionToRemove = allSubscriptions.FirstOrDefault(x => x == subscription);
				if (subscriptionToRemove != null)
				{
					allSubscriptions.Remove(subscriptionToRemove);
				}
			}
		}
	}

	public void Publish<TEvent>(TEvent eventItem) where TEvent : notnull
	{
		Subscription[] allSubscriptions;
		lock (_syncObj)
		{
			if (!_subscriptions.TryGetValue(typeof(TEvent), out var subscriptions))
			{
				return;
			}

			allSubscriptions = subscriptions.ToArray();
		}

		foreach (var subscription in allSubscriptions)
		{
			try
			{
				subscription.Invoke(eventItem);
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error in event subscriber: {ex}");
			}
		}
	}

	internal class Subscription : IDisposable
	{
		public Type Type { get; }
		private readonly Action<object> _action;
		private readonly EventBus _eventBus;

		public static Subscription Create(Type eventType, Action<object> action, EventBus eventBus) =>
			new(action, eventType, eventBus);

		public static Subscription Create<TEvent>(Action<TEvent> action, EventBus eventBus) =>
			new(o => action((TEvent)o), typeof(TEvent), eventBus);

		private Subscription(Action<object> action, Type type, EventBus eventBus)
		{
			Type = type;
			_action = action;
			_eventBus = eventBus;
		}

		public void Invoke<TEvent>(TEvent param) where TEvent : notnull
		{
			_action(param);
		}

		public void Dispose()
		{
			_eventBus.Unsubscribe(this);
		}
	}
}


public record MiningFeeRatesChanged(FeeRateEstimations AllFeeEstimate);
public record NetworkTipHeightChanged(uint Height);
public record ClientTipHeightChanged(uint Height);
public record WalletSyncStatusChanged(WalletSyncStatus Status);
public record TorConnectionStateChanged(bool IsTorRunning);

public record RpcStatusChanged(Result<ConnectedRpcStatus, string> Status);

/// <summary>
/// The application has what it takes to coinjoin with a coordinator from now on: its configuration names one, or the
/// user has connected it to one while it runs.
/// </summary>
public record CoinJoinAvailable;
public record Tick(DateTime DateTime);

public record CpfpInfoArrived;

public record WalletLoaded(Wallet Wallet);

public record NewTransactionInMempool(SmartTransaction Transaction);
public record WalletRelevantTransactionProcessed(ProcessedResult Result);

/// <summary>The wallet found out the anonymity points of coins: of coins that it has, not of new ones.</summary>
public record AnonymityPointsChanged;

/// <summary>The wallet processed a transaction that creates or spends a fidelity bond of it.</summary>
public record FidelityBondTransactionProcessed(SmartTransaction Transaction);

/// <summary>The offer that the wallet has on the market changed: there is one now, it is another one, or it is gone.</summary>
public record MakerOfferChanged(MakerOfferState? Offer);
public record PaymentBatchChanged(IReadOnlyList<Payment> Payments);
public record CoinjoinCostsAdded(uint256 TransactionId, CoinjoinCosts Costs);
