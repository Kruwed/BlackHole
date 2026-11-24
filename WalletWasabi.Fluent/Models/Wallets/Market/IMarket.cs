using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;

namespace WalletWasabi.Fluent.Models.Wallets.Market;

/// <summary>A coin as the maker's coin list names it.</summary>
/// <param name="TransactionId">The transaction that created the coin.</param>
/// <param name="Index">The place of the coin among the outputs of that transaction.</param>
/// <param name="BlocksLeft">For a coin that is locked in a bond: how many blocks it is still locked for, if that is known.</param>
public sealed record MarketCoinInfo(MarketCoin Coin, string TransactionId, uint Index, string Address, long? BlocksLeft = null)
{
	/// <summary>The output that the coin is, shortened: "62a5f470…4a64e5:0".</summary>
	public string OutPointText => TransactionId.Length > 14 ? $"{TransactionId[..8]}…{TransactionId[^6..]}:{Index}" : $"{TransactionId}:{Index}";

	/// <summary>The address of the coin, shortened: "bc1pgp3k93886…jwttthdy".</summary>
	public string AddressText => Address.Length > 20 ? $"{Address[..12]}…{Address[^8..]}" : Address;
}

/// <summary>A fidelity bond of the wallet as the Lock Coins tab tells it.</summary>
/// <param name="CoinId">The name that the bond has in the coin list.</param>
/// <param name="Amount">What the bond holds, in sats.</param>
/// <param name="LockedFor">For how many blocks the coins were locked, if that is known.</param>
/// <param name="BlocksLeft">How many blocks the bond is still locked for, if that is known. Zero once it has unlocked.</param>
/// <param name="CanRedeem">Whether the bond has unlocked and can be paid back to the wallet.</param>
/// <param name="Status">What there is to tell about the bond besides its numbers, if anything: that it waits for its confirmation, for instance.</param>
public sealed record MarketBondInfo(string CoinId, long Amount, long? LockedFor, long? BlocksLeft, bool CanRedeem, string? Status);

/// <summary>An offer that the wallet has on the market, as its card on the List Offers tab tells it.</summary>
/// <param name="BookId">The number that the offer has as a row of the order book.</param>
/// <param name="CoinIds">The coins that the offer holds.</param>
/// <param name="CoinsTotal">What those coins hold together, in sats.</param>
/// <param name="PrivateCoins">How many of them are private.</param>
/// <param name="Amount">The amount that is offered, in sats.</param>
/// <param name="Problem">Why the offer may not be on the market, or null if nothing is known against it.</param>
public sealed record MarketOfferInfo(
	int Id,
	long BookId,
	string Alias,
	ImmutableArray<string> CoinIds,
	long CoinsTotal,
	int PrivateCoins,
	long Amount,
	decimal Points,
	decimal FeeRate,
	decimal SellRate,
	decimal BuyRate,
	int MatchedInputs,
	string? Problem)
{
	public int OtherCoins => CoinIds.Length - PrivateCoins;

	/// <summary>"3 Matched", or "3 Matched, 1 Unmatched".</summary>
	public string CoinsText => OtherCoins > 0 ? $"{PrivateCoins} Matched, {OtherCoins} Unmatched" : $"{PrivateCoins} Matched";
}

/// <summary>What is asked before a fidelity bond is locked, with what it takes to lock it afterwards.</summary>
/// <param name="Title">"Confirm Bond for 1.20000000 BTC".</param>
/// <param name="LockText">"Locked for 2,016 blocks (2 weeks)".</param>
/// <param name="FeeText">"Fee rate 1 sat/vByte (154 sats)".</param>
/// <param name="Token">What the market that made the quote needs to lock the bond.</param>
public sealed record BondQuote(BondDraft Draft, string Title, string LockText, string FeeText, object? Token = null);

/// <summary>
/// What the two sides of the market work with: an order book, the coins of a wallet, its offers and its bonds. The
/// screens do not know whether these are the real ones or the sample ones.
/// </summary>
public interface IMarket
{
	/// <summary>Whether this is the sample data of the design: nothing of it is real, and nothing that is done with it leaves the screen.</summary>
	bool IsSample { get; }

	/// <summary>Tells that the book, the coins, the offers or the bonds may be other ones now.</summary>
	IObservable<Unit> Changed { get; }

	/// <summary>The offers of the other makers.</summary>
	IReadOnlyList<BookMaker> Book { get; }

	/// <summary>What there is to say about the book besides its offers: that the relays did not answer, for instance. Null if nothing.</summary>
	string? BookNotice { get; }

	/// <summary>The coins of the wallet, the ones that are locked in a bond included.</summary>
	IReadOnlyList<MarketCoinInfo> Coins { get; }

	/// <summary>The fidelity bonds of the wallet that are not paid back yet.</summary>
	IReadOnlyList<MarketBondInfo> Bonds { get; }

	/// <summary>The offers that the wallet has on the market.</summary>
	IReadOnlyList<MarketOfferInfo> Offers { get; }

	/// <summary>What the bond of the wallet holds that stands behind its offers, in sats. Zero without one.</summary>
	long OwnBond { get; }

	/// <summary>What the wallet brings to a transaction as a taker. Null if that is nothing yet.</summary>
	PlannedTaker? Taker { get; }

	/// <summary>What the taker's side says about the coins of the taker: what they are, or what is to be done to have some.</summary>
	string TakerNotice { get; }

	/// <summary>Why the planned transaction cannot be signed and sent.</summary>
	string SignNotice { get; }

	/// <summary>Reads the order book anew.</summary>
	Task RefreshBookAsync(CancellationToken cancellationToken);

	/// <summary>Posts an offer.</summary>
	/// <returns>Why the offer was not posted, or null if it was.</returns>
	Task<string?> PostOfferAsync(OfferDraft draft, CancellationToken cancellationToken);

	/// <summary>Takes an offer back.</summary>
	Task CancelOfferAsync(int id, CancellationToken cancellationToken);

	/// <summary>Prepares a fidelity bond: what locking it would mean, to be asked before it is locked.</summary>
	/// <returns>The question to ask, or why the bond cannot be locked.</returns>
	Task<(BondQuote? Quote, string? Error)> QuoteBondAsync(BondDraft draft, CancellationToken cancellationToken);

	/// <summary>Locks a fidelity bond that was confirmed.</summary>
	/// <param name="authorize">Asks the user for what the wallet needs to sign, if it needs anything. False if the user gave up.</param>
	/// <returns>Why the bond was not locked, or null if it was. An empty text if the user gave up.</returns>
	Task<string?> LockBondAsync(BondQuote quote, Func<Task<bool>> authorize, CancellationToken cancellationToken);

	/// <summary>Opens what pays a bond that has unlocked back to the wallet.</summary>
	void RedeemBond(MarketBondInfo bond);
}
