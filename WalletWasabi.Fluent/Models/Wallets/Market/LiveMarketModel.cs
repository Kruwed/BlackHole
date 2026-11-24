using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.JoinMarket;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets.Market;

/// <summary>
/// The market as the wallet takes part in it: its coins with their anonymity points, its fidelity bonds, the offers
/// that it posts to the Nostr relays, and the offers of the other makers that the relays tell.
/// </summary>
/// <remarks>
/// What a taker brings to a transaction are the coins that are ticked on the Coins tab, like everything else that the
/// Coinjoin dialog does with coins. The coins of an offer or of a bond are picked on the maker's side itself.
/// </remarks>
public sealed class LiveMarketModel : IMarket
{
	/// <summary>The numbers of the offers of this wallet in the order book start far above the numbers of the other offers.</summary>
	public const long OwnOfferIdBase = 1_000_000_000;

	private const string BondIdPrefix = "bond:";

	private readonly UiContext _uiContext;
	private readonly IWalletModel _walletModel;
	private readonly Wallet _wallet;
	private readonly CoinListViewModel _coins;

	/// <summary>The number that every offer of the book got when it was seen first. A maker keeps its number while the application runs.</summary>
	private readonly Dictionary<string, long> _bookIds = [];

	/// <param name="coins">The list of the Coins tab. Its coins are the coins of the wallet, and the ticked ones are what a taker brings.</param>
	public LiveMarketModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, CoinListViewModel coins)
	{
		_uiContext = uiContext;
		_walletModel = walletModel;
		_wallet = wallet;
		_coins = coins;

		Changed = Observable
			.Merge(
				coins.CoinItems.ToObservableChangeSet().ToSignal(),
				coins.Selection.ToObservableChangeSet().ToSignal(),
				walletModel.Market.OffersChanged.ToSignal(),
				walletModel.Market.BookChanged.ToSignal(),
				walletModel.Bonds.Changed)
			.Throttle(TimeSpan.FromMilliseconds(150))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Publish()
			.RefCount();
	}

	public bool IsSample => false;

	public IObservable<Unit> Changed { get; }

	public IReadOnlyList<BookMaker> Book
	{
		get
		{
			var entries = _walletModel.Market.Book.Entries;
			var aliases = MakerAlias.Disambiguate(entries.Select(entry => entry.PublicKey).Concat(_walletModel.Market.Offers.Select(offer => offer.PublicKey)));

			return [.. entries.Select(entry => new BookMaker(
				GetBookId(entry.PublicKey),
				aliases[entry.PublicKey],
				entry.Offer.MinFeeRate,
				entry.Offer.BuyRate,
				entry.Offer.SellRate,
				entry.Offer.Spread,
				entry.Bond,
				entry.Offer.Liquidity,
				entry.Offer.MatchedInputs,
				entry.Offer.ExtraInputs,
				entry.Offer.Points,
				[]))];
		}
	}

	public string? BookNotice
	{
		get
		{
			var book = _walletModel.Market.Book;

			if (book.ReadAt is null)
			{
				return "Asking your Nostr relays for offers…";
			}

			if (book.Problem is { } problem)
			{
				return problem;
			}

			if (book.Entries.Length == 0)
			{
				return "Your Nostr relays have no offers of other Makers.";
			}

			return book.BondProblem;
		}
	}

	public IReadOnlyList<MarketCoinInfo> Coins
	{
		get
		{
			var network = _walletModel.Network;
			var bonds = _walletModel.Bonds.GetBonds();

			var locked = bonds.Bonds
				.Where(IsStanding)
				.OrderByDescending(bond => bond.Bond.Amount)
				.Select(bond => new MarketCoinInfo(
					new MarketCoin(GetBondId(bond), bond.Bond.OutPoint.Hash.ToString(), bond.Bond.Amount.Satoshi, 0, IsBond: true),
					bond.Bond.OutPoint.Hash.ToString(),
					bond.Bond.OutPoint.N,
					bond.Address.ToString(),
					GetBlocksLeft(bond, bonds.ChainHeight)));

			var spendable = _coins.CoinItems
				.Select(row => row.Coin.GetSmartCoin())
				.Select(coin => new MarketCoinInfo(
					new MarketCoin(GetCoinId(coin.Outpoint), coin.TransactionId.ToString(), coin.Amount.Satoshi, coin.AnonymityPoints),
					coin.TransactionId.ToString(),
					coin.Index,
					coin.ScriptPubKey.GetDestinationAddress(network)?.ToString() ?? ""));

			return [.. locked, .. spendable];
		}
	}

	public IReadOnlyList<MarketBondInfo> Bonds
	{
		get
		{
			var bonds = _walletModel.Bonds.GetBonds();

			return [.. bonds.Bonds
				.Where(IsStanding)
				.OrderByDescending(bond => bond.Bond.Amount)
				.Select(bond => new MarketBondInfo(
					GetBondId(bond),
					bond.Bond.Amount.Satoshi,
					GetLockedFor(bond, bonds.ChainHeight),
					GetBlocksLeft(bond, bonds.ChainHeight),
					CanRedeem: bond.State == FidelityBondState.Unlocked,
					bond.State switch
					{
						FidelityBondState.Unconfirmed => "Waiting for confirmation",
						FidelityBondState.Unlocked => "Unlocked: the coins can be redeemed",
						_ => null
					}))];
		}
	}

	public IReadOnlyList<MarketOfferInfo> Offers
	{
		get
		{
			var privateCoins = _coins.CoinItems
				.Select(row => row.Coin.GetSmartCoin())
				.Where(coin => coin.AnonymityPoints >= 1)
				.Select(coin => coin.Outpoint)
				.ToHashSet();

			return [.. _walletModel.Market.Offers.Select(offer => new MarketOfferInfo(
				offer.Id,
				OwnOfferIdBase + offer.Id,
				offer.Alias,
				[.. offer.Coins.Select(GetCoinId)],
				offer.CoinsTotal,
				offer.Coins.Count(privateCoins.Contains),
				offer.Offer.Liquidity,
				offer.Offer.Points,
				offer.Offer.MinFeeRate,
				offer.Offer.SellRate,
				offer.Offer.BuyRate,
				offer.Offer.MatchedInputs,
				offer.Problem))];
		}
	}

	/// <summary>The largest bond that is confirmed and still locked: the one that the offers of the wallet name.</summary>
	public long OwnBond =>
		_walletModel.Bonds.GetBonds().Bonds
			.Where(bond => bond.State == FidelityBondState.Locked)
			.Select(bond => bond.Bond.Amount.Satoshi)
			.DefaultIfEmpty(0)
			.Max();

	public PlannedTaker? Taker =>
		_coins.Selection.Count > 0
			? new PlannedTaker(_coins.Selection.Count, _coins.Selection.TotalAmount().Satoshi)
			: null;

	public string TakerNotice =>
		_coins.Selection.Count switch
		{
			0 => "Tick the coins that you bring on the Coins tab, or tick an offer of your own in the book.",
			1 => $"You bring the coin that is ticked on the Coins tab: {MarketFormat.Btc(_coins.Selection.TotalAmount().Satoshi)} BTC.",
			var count => $"You bring the {count} coins that are ticked on the Coins tab: {MarketFormat.Btc(_coins.Selection.TotalAmount().Satoshi)} BTC."
		};

	public string SignNotice => "Not available yet: how a Taker reaches the Makers that it picked is still to be designed.";

	public Task RefreshBookAsync(CancellationToken cancellationToken) => _walletModel.Market.RefreshBookAsync(cancellationToken);

	public async Task<string?> PostOfferAsync(OfferDraft draft, CancellationToken cancellationToken)
	{
		var coins = FindCoins(draft.CoinIds);

		if (coins.Length != draft.CoinIds.Length)
		{
			return "The wallet does not have all of these coins any more.";
		}

		try
		{
			var request = new OfferRequest([.. coins.Select(coin => coin.Outpoint)], draft.FeeRate, draft.BuyRate, draft.SellRate, draft.Amount, draft.Points, draft.MatchedInputs);
			var (offer, answers) = await _walletModel.Market.PostAsync(request, cancellationToken);

			return offer is null ? $"The offer was not posted. {MakerOfferService.DescribeFailure(answers)}" : null;
		}
		catch (ArgumentException ex)
		{
			// A coin was spent while the form was open, for instance.
			return ex.Message;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Logger.LogError(ex);
			return $"The offer was not posted. {ex.Message}";
		}
	}

	public Task CancelOfferAsync(int id, CancellationToken cancellationToken) => _walletModel.Market.CancelAsync(id, cancellationToken);

	/// <summary>
	/// The bond is locked until the block that is the given number of blocks ahead of the chain. The transaction is
	/// built, not signed, to tell what it costs: the wallet spends as few of the picked coins as the amount takes.
	/// </summary>
	public async Task<(BondQuote? Quote, string? Error)> QuoteBondAsync(BondDraft draft, CancellationToken cancellationToken)
	{
		if (_walletModel.Bonds.ChainHeight is not { } chainHeight)
		{
			return (null, "The Bitcoin node does not answer. Without it there is no telling how far the chain is.");
		}

		if (draft.Blocks > WalletBondsModel.MaxLockBlocks)
		{
			return (null, $"Coins can be locked for {MarketFormat.Whole(WalletBondsModel.MaxLockBlocks)} blocks at most, about ten years.");
		}

		var unlockHeight = chainHeight + (uint)draft.Blocks;
		var coins = FindCoins(draft.CoinIds);

		if (coins.Length != draft.CoinIds.Length)
		{
			return (null, "The wallet does not have all of these coins any more.");
		}

		try
		{
			// The wallet watches the address of the height from here on, and its file remembers the height.
			var address = await Task.Run(() => _walletModel.Bonds.GetAddress(unlockHeight), cancellationToken);
			var amount = Money.Satoshis(draft.Amount);

			var info = new TransactionInfo(new Destination.Loudly(address.ScriptPubKey))
			{
				Amount = amount,

				// When everything is locked, the bond pays the mining fee.
				SubtractFee = draft.Amount == draft.SelectedTotal,
				FeeRate = new FeeRate(draft.FeeRate),
				Coins = coins,
				IsCustomFeeUsed = true
			};

			var transaction = await Task.Run(() => TransactionHelpers.BuildTransaction(_wallet, info, tryToSign: false), cancellationToken);
			var locked = transaction.Transaction.Transaction.Outputs.Where(output => output.ScriptPubKey == address.ScriptPubKey).Sum(output => output.Value.Satoshi);

			return (
				new BondQuote(
					draft,
					$"Confirm Bond for {MarketFormat.Btc(locked)} BTC",
					draft.LockText,
					BondDraft.DescribeFee(draft.FeeRate, transaction.Fee.Satoshi),
					new BondToken(unlockHeight, info)),
				null);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Logger.LogWarning(ex);
			return (null, ex.ToUserFriendlyString());
		}
	}

	public async Task<string?> LockBondAsync(BondQuote quote, Func<Task<bool>> authorize, CancellationToken cancellationToken)
	{
		if (quote.Token is not BondToken token)
		{
			return "The bond was not prepared.";
		}

		// The chain may have moved on since the question was asked. Coins that are locked until a block that is there
		// already are not locked, and getting them back costs a second fee.
		if (!_walletModel.Bonds.CanLockUntil(token.UnlockHeight))
		{
			return "The Bitcoin node does not tell how far the chain is any more. The bond was not locked.";
		}

		var coinJoinManager = _uiContext.Services.GetHostedService<CoinJoinManager>();

		// Locking coins is spending them. The coinjoins of the wallet wait for it like they wait for a payment.
		coinJoinManager?.WalletEnteredSendWorkflow(_walletModel.Id);

		try
		{
			if (coinJoinManager is not null)
			{
				await coinJoinManager.WalletEnteredSendingAsync(_wallet);
			}

			var transaction = await Task.Run(() => TransactionHelpers.BuildTransaction(_wallet, token.Info), cancellationToken);

			if (!await authorize())
			{
				return "";
			}

			await _uiContext.Services.SendTransactionAsync(transaction.Transaction);
			return null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Logger.LogError(ex);
			return $"The bond was not locked. {ex.ToUserFriendlyString()}";
		}
		finally
		{
			coinJoinManager?.WalletLeftSendWorkflow(_wallet);
		}
	}

	public void RedeemBond(MarketBondInfo bond)
	{
		if (_walletModel.Bonds.GetBonds().Bonds.FirstOrDefault(candidate => GetBondId(candidate) == bond.CoinId) is { State: FidelityBondState.Unlocked } model)
		{
			_uiContext.Navigate().To().RedeemBond(_walletModel, model);
		}
	}

	/// <summary>The offers of this wallet as rows of the order book.</summary>
	public static IReadOnlyList<BookMaker> GetOwnMakers(IMarket market) =>
		[.. market.Offers.Select(offer => new BookMaker(
			offer.BookId,
			offer.Alias,
			offer.FeeRate,
			offer.BuyRate,
			offer.SellRate,
			offer.SellRate - offer.BuyRate,
			market.OwnBond,
			offer.Amount,
			offer.MatchedInputs,
			offer.CoinIds.Length - offer.MatchedInputs,
			offer.Points,
			[],
			IsOwn: true))];

	/// <summary>Whether a bond still holds coins of the wallet: it is not paid back, and not about to be.</summary>
	private static bool IsStanding(FidelityBondModel bond) =>
		bond.State is FidelityBondState.Unconfirmed or FidelityBondState.Locked or FidelityBondState.Unlocked;

	private static string GetBondId(FidelityBondModel bond) => BondIdPrefix + bond.Bond.OutPoint;

	private static string GetCoinId(OutPoint outPoint) => outPoint.ToString();

	private static long? GetBlocksLeft(FidelityBondModel bond, uint? chainHeight) =>
		chainHeight is { } height ? Math.Max(0, (long)bond.Bond.UnlockHeight - height) : null;

	/// <summary>For how many blocks the coins were locked: from the block that confirmed the bond, or from where the chain is while it is not confirmed.</summary>
	private static long? GetLockedFor(FidelityBondModel bond, uint? chainHeight) =>
		bond.Bond.Transaction.Height is Height.ChainHeight(var confirmationHeight)
			? Math.Max(0, (long)bond.Bond.UnlockHeight - confirmationHeight)
			: GetBlocksLeft(bond, chainHeight);

	private long GetBookId(string publicKey)
	{
		if (!_bookIds.TryGetValue(publicKey, out var id))
		{
			id = _bookIds.Count + 1;
			_bookIds[publicKey] = id;
		}

		return id;
	}

	/// <summary>The coins of the wallet with the given names, as far as the wallet still has them.</summary>
	private SmartCoin[] FindCoins(ImmutableArray<string> ids)
	{
		var byId = _coins.CoinItems
			.Select(row => row.Coin.GetSmartCoin())
			.ToDictionary(coin => GetCoinId(coin.Outpoint));

		return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
	}

	/// <summary>What it takes to lock a bond that was asked about.</summary>
	private sealed record BondToken(uint UnlockHeight, TransactionInfo Info);
}
