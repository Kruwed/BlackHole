using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public interface ICoinListModel
{
	IObservableCache<CoinModel, int> List { get; }

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

		List = signals
			.Select(_ => GetCoins().Select(CreateCoinModel).ToArray())
			.EditDiff(x => x.Key)
			.DisposeMany()
			.AsObservableCache()
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
