using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public interface ICoinListModel
{
	IObservableCache<CoinModel, int> List { get; }

	/// <summary>The coins grouped by their privacy level.</summary>
	IObservableCache<CoinGroup, PrivacyLevel> Groups { get; }

	CoinModel GetCoinModel(SmartCoin smartCoin);

	/* SKIPPED: Implements an interface */ /* void Dispose(); */
}

public abstract partial class CoinListModel : ICoinListModel, IDisposable
{
	private readonly IServices _services;
	private readonly CompositeDisposable _disposables = new();

	public CoinListModel(Wallet wallet, IWalletModel walletModel, IServices services)
	{
		_services = services;
		Wallet = wallet;
		WalletModel = walletModel;
		var transactionProcessed = walletModel.Transactions.TransactionProcessed;
		var isCoinjoinRunningChanged = walletModel.IsCoinjoinRunning.ToSignal();

		var signals =
			transactionProcessed
				.Merge(isCoinjoinRunningChanged)
				.Publish();

		// The list and the groups are made of the same reading of the coins. The wallet may be processing blocks in the
		// background, so two readings can differ, and a group must not hold a coin that the list does not know.
		// The list is subscribed first: it has the coins by the time the groups announce them.
		var coins = signals.Select(_ => GetCoins().ToArray()).Publish();

		List = coins
			.Select(x => x.Select(CreateCoinModel))
			.EditDiff(x => x.Key)
			.DisposeMany()
			.AsObservableCache()
			.DisposeWith(_disposables);

		Groups = coins
			.Select(x => x.GetCoinGroups())
			.EditDiff(x => x.PrivacyLevel)
			.AsObservableCache()
			.DisposeWith(_disposables);

		coins.Connect()
			.DisposeWith(_disposables);

		signals
			.Do(_ => Logger.LogDebug("Refresh signal emitted in the coin list"))
			.Subscribe()
			.DisposeWith(_disposables);

		signals.Connect()
			.DisposeWith(_disposables);
	}

	protected Wallet Wallet { get; }
	protected IWalletModel WalletModel { get; }

	public IObservableCache<CoinModel, int> List { get; }

	public IObservableCache<CoinGroup, PrivacyLevel> Groups { get; }

	public CoinModel GetCoinModel(SmartCoin smartCoin)
	{
		return List.Items.First(coinModel => coinModel.Key == smartCoin.Outpoint.GetHashCode());
	}

	protected CoinModel CreateCoinModel(SmartCoin smartCoin)
	{
		return new CoinModel(smartCoin, WalletModel.Network, _services);
	}

	/// <summary>The coins of the list as they are now.</summary>
	protected abstract IEnumerable<SmartCoin> GetCoins();

	public void Dispose() => _disposables.Dispose();
}
