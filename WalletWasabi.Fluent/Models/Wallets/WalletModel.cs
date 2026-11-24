using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Services;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial interface IWalletModel : INotifyPropertyChanged
{
	bool IsLoggedIn { get; set; }

	bool IsLoaded { get; set; }

	IObservable<bool> IsCoinjoinRunning { get; }

	IObservable<bool> IsCoinjoinStarted { get; }

	bool IsCoinJoinEnabled { get; }

	AddressesModel Addresses { get; }

	WalletId Id { get; }

	Network Network { get; }

	WalletTransactionsModel Transactions { get; }

	/// <summary>What the wallet owns: its coins and the coins that are locked in its fidelity bonds.</summary>
	IObservable<Amount> Balances { get; }

	/// <summary>Whether the wallet has coins to spend. What is locked in fidelity bonds does not count.</summary>
	IObservable<bool> HasBalance { get; }

	WalletCoinsModel Coins { get; }

	WalletAuthModel Auth { get; }

	WalletLoadWorkflow Loader { get; }

	WalletSettingsModel Settings { get; }

	/// <summary>
	/// The coinjoins of the wallet with the coordinator. There are none until the application has a coordinator, which
	/// can be after the wallet is opened: whoever shows them has to look again when this changes.
	/// </summary>
	WalletCoinjoinModel? Coinjoin { get; }

	/// <summary>The fidelity bonds of the wallet: coins that are locked until the chain reaches a block height.</summary>
	WalletBondsModel Bonds { get; }

	/// <summary>The market for anonymity points: the offers of the wallet as a maker, and the order book of the other makers.</summary>
	WalletMarketModel Market { get; }

	IObservable<bool> Loaded { get; }

	/// <summary>
	/// Whether the wallet knows where it is in the chain. A new wallet learns that from the node, and its history starts there:
	/// it has nothing to resync until then.
	/// </summary>
	bool IsHeightKnown { get; }

	/// <summary>Whether the wallet was recovered from its backup, and not created.</summary>
	bool IsRecovered { get; }

	/// <summary>Whether <see cref="Resync"/> was called: the wallet waits for the restart of the application and does not follow the node until then.</summary>
	bool IsResyncRequested { get; }

	/// <summary>
	/// Makes the wallet look for its transactions again, from the given block on and with the given gap limit, when the application starts the next time.
	/// </summary>
	void Resync(uint startingHeight, int minGapLimit);


	WalletInfoModel GetWalletInfo();
}

[AppLifetime]
public partial class WalletModel : ReactiveObject, IWalletModel
{
	private readonly IServices _services;
	private readonly Lazy<WalletCoinsModel> _coins;

	/// <summary>See <see cref="IWalletModel.Coinjoin"/>.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private WalletCoinjoinModel? _coinjoin;

	[AutoNotify] private bool _isLoggedIn;
	[AutoNotify] private bool _isLoaded;

	public WalletModel(IServices services, Wallet wallet)
	{
		_services = services;
		Wallet = wallet;

		Auth = new WalletAuthModel(Wallet);
		Loader = new WalletLoadWorkflow(services, Wallet);
		Settings = new WalletSettingsModel(services, Wallet.KeyManager);

		// The coinjoins take a coordinator. The application gets to one when it has started, or when the user connects
		// it to one in the settings: both can be later than this.
		UpdateCoinjoin();

		services.EventBus.AsObservable<CoinJoinAvailable>()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ => UpdateCoinjoin());

		_coins = new(() => new WalletCoinsModel(wallet, this, services));

		Transactions = new WalletTransactionsModel(services, this, wallet);

		Addresses = new AddressesModel(services, Wallet);

		Bonds = new WalletBondsModel(services, Wallet);

		Market = new WalletMarketModel(services);

		Loaded = services.EventBus.AsObservable<WalletLoaded>()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Select(_ => Wallet.Loaded);

		// The coins in the fidelity bonds of the wallet are the wallet's too, though it cannot spend them for the time being.
		var owned = Transactions.TransactionProcessed
			.Select(_ => (Coins: Wallet.Coins.TotalAmount(), Bonds: Wallet.GetFidelityBondBalance()));

		Balances = owned.Select(x => new Amount(x.Coins + x.Bonds));
		HasBalance = owned.Select(x => x.Coins != Money.Zero);

		// Start the Loader after wallet is logged in
		this.WhenAnyValue(x => x.Auth.IsLoggedIn)
			.Where(x => x)
			.Take(1)
			.Do(_ => Loader.Start())
			.Subscribe();

		// The loader keeps reporting after the load is completed: the title bar goes on showing how many blocks the wallet processed.

		this.WhenAnyValue(x => x.Auth.IsLoggedIn)
			.BindTo(this, x => x.IsLoggedIn);

		this.WhenAnyObservable(x => x.Loaded)
			.BindTo(this, x => x.IsLoaded);
	}

	public IObservable<bool> IsCoinjoinRunning =>
		this.WhenAnyValue(x => x.Coinjoin)
			.Select(coinjoin => coinjoin?.IsRunning ?? Observable.Return(false))
			.Switch();

	public IObservable<bool> IsCoinjoinStarted =>
		this.WhenAnyValue(x => x.Coinjoin)
			.Select(coinjoin => coinjoin?.IsStarted ?? Observable.Return(false))
			.Switch();

	public bool IsCoinJoinEnabled => Coinjoin is not null;

	public AddressesModel Addresses { get; }

	internal Wallet Wallet { get; }

	public WalletId Id => Wallet.WalletId;

	public Network Network => Wallet.Network;

	public WalletTransactionsModel Transactions { get; }

	public IObservable<Amount> Balances { get; }

	public IObservable<bool> HasBalance { get; }

	public WalletCoinsModel Coins => _coins.Value;

	public WalletAuthModel Auth { get; }

	public WalletLoadWorkflow Loader { get; }

	public WalletSettingsModel Settings { get; }

	/// <summary>Makes the coinjoins of the wallet once the application has a coordinator.</summary>
	private void UpdateCoinjoin()
	{
		if (Coinjoin is null && _services.GetHostedService<CoinJoinManager>() is { } coinJoinManager)
		{
			Coinjoin = new WalletCoinjoinModel(_services, Wallet, coinJoinManager);
		}
	}

	public WalletBondsModel Bonds { get; }

	public WalletMarketModel Market { get; }

	public IObservable<bool> Loaded { get; }

	public bool IsHeightKnown => Wallet.KeyManager.GetBestHeight() is not null;

	public bool IsRecovered => Wallet.KeyManager.IsRecovered;

	public bool IsResyncRequested => Wallet.Synchronizer.IsResyncRequested;

	public void Resync(uint startingHeight, int minGapLimit)
	{
		Wallet.Synchronizer.RequestResync(startingHeight, minGapLimit);
	}

	public WalletInfoModel GetWalletInfo()
	{
		return new WalletInfoModel(Wallet);
	}
}
