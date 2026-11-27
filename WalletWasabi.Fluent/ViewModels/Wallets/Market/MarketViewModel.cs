using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Market;

/// <summary>
/// The two sides of the market for anonymity points, which are the main screens of the Maker and the Taker tab of the
/// Coinjoin dialog. They work with the wallet, its Nostr relays and its node, or, where the user asks for that, with
/// the sample data of the design of the market, which is nothing real and leaves nothing behind.
/// </summary>
/// <remarks>
/// The screens are the same ones for as long as the wallet is open: what was set on them is still there when the
/// dialog is opened again. Each of the two kinds of data has screens of its own, so what was set with the sample data
/// is not carried over to the wallet.
/// </remarks>
[AppLifetime]
public partial class MarketViewModel : ViewModelBase
{
	/// <summary>How often the relays are asked for the order book while one of the two screens shows.</summary>
	public static readonly TimeSpan BookRefreshPeriod = TimeSpan.FromSeconds(60);

	public const string SampleNotice =
		"Sample data: the 250 Makers and the 16 coins on these two tabs are examples, not yours. Offers and bonds that you make with them stay on this screen.";

	private readonly IWalletModel _walletModel;
	private readonly LiveMarketModel _live;
	private readonly MakerScreenViewModel _liveMaker;
	private readonly TakerScreenViewModel _liveTaker;
	private readonly SerialDisposable _activation = new();

	private SampleMarketModel? _sample;
	private MakerScreenViewModel? _sampleMaker;
	private TakerScreenViewModel? _sampleTaker;
	private bool _isActive;

	[AutoNotify] private bool _isSample;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private MakerScreenViewModel _maker;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private TakerScreenViewModel _taker;

	/// <param name="coins">The list of the Coins tab. Its coins are the coins of the wallet, and the ticked ones are what a taker brings.</param>
	public MarketViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, CoinListViewModel coins) : base(uiContext)
	{
		_walletModel = walletModel;
		_live = new LiveMarketModel(uiContext, walletModel, wallet, coins);
		_liveMaker = new MakerScreenViewModel(_live, ConfirmBondAsync, AuthorizeAsync);
		_liveTaker = new TakerScreenViewModel(_live);
		_maker = _liveMaker;
		_taker = _liveTaker;

		this.WhenAnyValue(x => x.IsSample)
			.Skip(1)
			.Subscribe(_ => SwitchData());
	}

	/// <summary>
	/// Makes the screens follow the market, and has the order book read, from now until <see cref="Deactivate"/>: while
	/// one of the two tabs shows.
	/// </summary>
	public void Activate()
	{
		_isActive = true;

		var disposables = new CompositeDisposable();

		Maker.Activate(disposables);
		Taker.Activate(disposables);

		if (!IsSample)
		{
			// The relays are asked at once and then every minute. Nothing is sent to them but the question.
			Observable.Timer(TimeSpan.Zero, BookRefreshPeriod)
				.Select(_ => Observable.FromAsync(async cancellationToken =>
				{
					try
					{
						await _live.RefreshBookAsync(cancellationToken);
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						Logger.LogDebug(ex);
					}
				}))
				.Concat()
				.Subscribe()
				.DisposeWith(disposables);
		}

		_activation.Disposable = disposables;
	}

	/// <summary>The screens do not show any more: nothing is followed, and the relays are not asked.</summary>
	public void Deactivate()
	{
		_isActive = false;
		_activation.Disposable = null;
	}

	/// <summary>
	/// Sets the maker's side to where coins of the wallet are locked in a fidelity bond: the wallet in place of the
	/// sample data, and Lock Coins in place of List Offers. A wallet with a bond is told about its bond there.
	/// </summary>
	public void ShowLockCoins()
	{
		IsSample = false;
		_liveMaker.ShowLockCoins();
	}

	private void SwitchData()
	{
		if (IsSample)
		{
			_sample ??= new SampleMarketModel();
			_sampleMaker ??= new MakerScreenViewModel(_sample, ConfirmBondAsync, () => Task.FromResult(true));
			_sampleTaker ??= new TakerScreenViewModel(_sample);

			Maker = _sampleMaker;
			Taker = _sampleTaker;
		}
		else
		{
			Maker = _liveMaker;
			Taker = _liveTaker;
		}

		if (_isActive)
		{
			Activate();
		}
	}

	private Task<bool> ConfirmBondAsync(BondQuote quote) =>
		UiContext.Navigate().To().ConfirmBond(quote).GetResultAsync();

	/// <summary>A wallet with a password asks for it once more before it signs, as it does before a payment.</summary>
	private Task<bool> AuthorizeAsync() =>
		_walletModel.Auth.HasPassword
			? UiContext.Navigate().To().PasswordAuthDialog(_walletModel, "Lock Coins").GetResultAsync()
			: Task.FromResult(true);
}
