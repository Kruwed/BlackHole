using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.JoinMarket;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.JoinMarket.Sample;

namespace WalletWasabi.Fluent.Models.Wallets.Market;

/// <summary>
/// The market on the sample data of its design: an order book of 250 makers and a wallet of 16 coins. Nothing of it
/// is real. An offer that is posted here goes nowhere, and a bond that is locked here locks nothing: both only change
/// what this screen shows, until the application is closed.
/// </summary>
public sealed class SampleMarketModel : IMarket
{
	/// <summary>The numbers of the offers of the sample wallet in the order book start above the numbers of the sample makers.</summary>
	public const long OwnOfferIdBase = 100_000;

	private readonly Subject<Unit> _changed = new();
	private readonly List<MarketCoinInfo> _coins;
	private readonly List<MarketBondInfo> _bonds = [];
	private readonly List<MarketOfferInfo> _offers = [];
	private int _lastOfferId;
	private int _lastCoinId;

	public SampleMarketModel()
	{
		_coins = [.. SampleMarket.Coins.Select(coin => new MarketCoinInfo(coin.ToMarketCoin(), coin.TransactionId, (uint)coin.Index, coin.Address))];
		_lastCoinId = SampleMarket.Coins.Max(coin => coin.Id);
	}

	public bool IsSample => true;

	public IObservable<Unit> Changed => _changed.AsObservable();

	public IReadOnlyList<BookMaker> Book => SampleMarket.Makers;

	public string? BookNotice => null;

	public IReadOnlyList<MarketCoinInfo> Coins => _coins;

	public IReadOnlyList<MarketBondInfo> Bonds => _bonds;

	public IReadOnlyList<MarketOfferInfo> Offers => _offers;

	public long OwnBond => _bonds.Count > 0 ? _bonds.Max(bond => bond.Amount) : 0;

	/// <summary>The taker of the sample: two inputs that hold 0.1 BTC together.</summary>
	public PlannedTaker? Taker => SampleMarket.Taker;

	public string TakerNotice => "The sample Taker brings 2 coins with 0.10000000 BTC. Tick an offer of your own in the book to bring its coins instead.";

	public string SignNotice => "Sample data: there is nothing to sign.";

	public Task RefreshBookAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task<string?> PostOfferAsync(OfferDraft draft, CancellationToken cancellationToken)
	{
		var coins = _coins.Where(coin => draft.CoinIds.Contains(coin.Coin.Id)).ToArray();
		var id = ++_lastOfferId;

		_offers.Add(new MarketOfferInfo(
			id,
			OwnOfferIdBase + id,
			CreateAlias(),
			draft.CoinIds,
			coins.Sum(coin => coin.Coin.Amount),
			coins.Count(coin => coin.Coin.IsPrivate),
			draft.Amount,
			draft.Points,
			draft.FeeRate,
			draft.SellRate,
			draft.BuyRate,
			draft.MatchedInputs,
			Problem: null));

		_changed.OnNext(Unit.Default);
		return Task.FromResult<string?>(null);
	}

	public Task CancelOfferAsync(int id, CancellationToken cancellationToken)
	{
		_offers.RemoveAll(offer => offer.Id == id);
		_changed.OnNext(Unit.Default);

		return Task.CompletedTask;
	}

	public Task<(BondQuote? Quote, string? Error)> QuoteBondAsync(BondDraft draft, CancellationToken cancellationToken) =>
		Task.FromResult<(BondQuote?, string?)>((new BondQuote(draft, draft.Title, draft.LockText, draft.FeeText), null));

	/// <summary>
	/// The picked coins are gone, and in their place there is the bond and, if more than a few sats are left after the
	/// fee, a coin for the rest. Neither has anonymity points.
	/// </summary>
	public Task<string?> LockBondAsync(BondQuote quote, Func<Task<bool>> authorize, CancellationToken cancellationToken)
	{
		var draft = quote.Draft;
		var transactionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
		var rest = draft.SelectedTotal - draft.Amount - (long)Math.Ceiling(draft.Estimate.Fee);

		_coins.RemoveAll(coin => draft.CoinIds.Contains(coin.Coin.Id));

		var bondId = (++_lastCoinId).ToString(CultureInfo.InvariantCulture);
		_coins.Insert(0, new MarketCoinInfo(new MarketCoin(bondId, transactionId, draft.Amount, 0, IsBond: true), transactionId, 0, CreateAddress(), draft.Blocks));
		_bonds.Add(new MarketBondInfo(bondId, draft.Amount, draft.Blocks, draft.Blocks, CanRedeem: false, Status: null));

		if (rest > BondFigures.ChangeThreshold)
		{
			var restId = (++_lastCoinId).ToString(CultureInfo.InvariantCulture);
			_coins.Insert(1, new MarketCoinInfo(new MarketCoin(restId, transactionId, rest, 0), transactionId, 1, CreateAddress()));
		}

		_changed.OnNext(Unit.Default);
		return Task.FromResult<string?>(null);
	}

	public void RedeemBond(MarketBondInfo bond)
	{
		// A bond of the sample never unlocks.
	}

	/// <summary>A name that neither a maker of the sample book nor another offer of the sample wallet has.</summary>
	private string CreateAlias()
	{
		var taken = SampleMarket.Makers.Select(maker => maker.Alias).Concat(_offers.Select(offer => offer.Alias)).ToHashSet();

		for (var i = 0; i < 200; i++)
		{
			var alias = MakerAlias.Get(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

			if (!taken.Contains(alias))
			{
				return alias;
			}
		}

		return string.Create(CultureInfo.InvariantCulture, $"maker-{_lastOfferId}");
	}

	/// <summary>Something that looks like an address and is none: nobody has its key, and it is not written the way an address is checked.</summary>
	private static string CreateAddress()
	{
		const string Alphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

		return "bc1p" + new string([.. RandomNumberGenerator.GetBytes(58).Select(value => Alphabet[value % Alphabet.Length])]);
	}
}
