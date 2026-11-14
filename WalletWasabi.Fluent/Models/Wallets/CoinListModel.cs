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
		var anonScoreTargetChanged = this.WhenAnyValue(x => x.WalletModel.Settings.AnonScoreTarget).Skip(1).ToSignal();
		var isCoinjoinRunningChanged = walletModel.IsCoinjoinRunning.ToSignal();

		var signals =
			transactionProcessed
				.Merge(anonScoreTargetChanged)
				.Merge(isCoinjoinRunningChanged)
				.Publish();

		List = signals.Fetch(CreateCoinModels, x => x.Key).DisposeWith(_disposables);
		Groups = signals.Fetch(GetGroups, x => x.PrivacyLevel).DisposeWith(_disposables);

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
		return new CoinModel(smartCoin, WalletModel.Network, WalletModel.Settings.AnonScoreTarget, _services);
	}

	protected abstract CoinGroup[] GetGroups();

	protected abstract CoinModel[] CreateCoinModels();

	public void Dispose() => _disposables.Dispose();
}
