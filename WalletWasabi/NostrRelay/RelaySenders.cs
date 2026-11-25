using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;

namespace WalletWasabi.NostrRelay;

/// <summary>What the node said about a fidelity bond that a key has shown.</summary>
public enum BondCheckResult
{
	/// <summary>The chain has the bond: an unspent output in a block, with the script of the proof, still locked.</summary>
	Found,

	/// <summary>The chain has no such bond.</summary>
	NotFound,

	/// <summary>The node could not be asked, so nothing is known about the bond.</summary>
	Unavailable
}

/// <param name="Amount">What the bond holds, in sats, if the chain has it.</param>
public readonly record struct BondCheck(BondCheckResult Result, long Amount = 0);

/// <summary>Asks the node whether the chain has the fidelity bond of a proof that is signed for the given key.</summary>
/// <param name="publicKey">The key that the proof is for, as events name it.</param>
public delegate Task<BondCheck> CheckBond(string publicKey, BondProof proof, CancellationToken cancellationToken);

/// <summary>What a relay decided about the key of an event.</summary>
/// <param name="Rank">What stands behind the key, in sats, for the order in which events make way when the room is used up.</param>
/// <param name="Message">
/// Why the event is refused, in the words that the relay answers with. For an event that is taken: why the bond that
/// it shows does not stand behind it, if it shows one that could not be counted. Nothing otherwise.
/// </param>
public readonly record struct SenderVerdict(bool IsAccepted, long Rank, string Message)
{
	public static SenderVerdict Accept(long rank, string? bondProblem = null) => new(true, rank, bondProblem ?? "");

	public static SenderVerdict Refuse(string message) => new(false, 0, message);
}

/// <summary>
/// Decides whose events a relay takes, by the rules that its user set, see <see cref="RelaySenderRules"/>, and tells
/// what stands behind every key that it lets in.
/// </summary>
/// <remarks>
/// <para>
/// A key shows its fidelity bond with a proof in one of its events, see <see cref="RelayBondClaim"/>. The node is
/// asked about a bond when a key shows it for the first time. After that the bond stands behind the key for as long
/// as the relay keeps events of it, whether the events show the proof again or not, and the node is not asked again.
/// </para>
/// <para>
/// A bond is shared by the keys that have shown it, in equal parts, see <see cref="RelayEventStore.Join"/>: what
/// stands behind a key is its share.
/// </para>
/// <para>
/// Asking the node is work that a stranger can make the relay do, so it has a pace, for one visitor and for all of
/// them together, see <see cref="RelayLimits.BondLookupBurst"/>. Beyond that a key that shows a new bond is told to
/// come back later, and the keys that the relay knows are served as before.
/// </para>
/// <para>
/// Events are public, and anybody can send an event of somebody else again. So only a new event is a sign of its
/// key, see <see cref="MaxSignAge"/>: an old one neither shows a bond nor counts as the relay hearing of the key.
/// Otherwise whoever kept the offers of a maker could bring the keys of before back with them, and have the bond of
/// the maker stand behind those keys in the place of the ones that the maker uses now.
/// </para>
/// </remarks>
public sealed class RelaySenders : IDisposable
{
	/// <summary>How long ago an event may have been made to be a sign of its key: for as long as an offer counts for whoever reads it.</summary>
	public static readonly TimeSpan MaxSignAge = MakerOfferEvent.MaxAge;

	public const string TooManyLookupsMessage = "rate-limited: too many fidelity bonds to look up at the moment, try again in a minute";
	public const string OldProofMessage = "restricted: a fidelity bond is shown with a new event, and this one was made too long ago";
	public const string NoLookupMessage = "error: the fidelity bond cannot be looked up at the moment, try again later";
	public const string NoSuchBondMessage = "restricted: the chain has no fidelity bond that is locked and is the one that the event shows";

	private readonly RelaySenderRules _rules;
	private readonly RelayEventStore _store;
	private readonly RelayLimits _limits;
	private readonly CheckBond? _checkBond;
	private readonly Func<uint?> _getTipHeight;
	private readonly RelayPace _lookupPace;

	/// <summary>One bond at a time: which keys a bond is shared by is decided for one key after the other.</summary>
	private readonly SemaphoreSlim _lookupLock = new(1, 1);

	/// <param name="store">The store of the relay, which keeps the book of the bonds. It is brought in line with the rules.</param>
	/// <param name="checkBond">Asks the node about a bond. Without it no bond can be found, and no key has one.</param>
	/// <param name="getTipHeight">Tells the height of the chain, if it is known. A bond that has unlocked stands behind nothing.</param>
	public RelaySenders(RelaySenderRules rules, RelayEventStore store, RelayLimits limits, CheckBond? checkBond, Func<uint?>? getTipHeight = null)
	{
		_rules = rules;
		_store = store;
		_limits = limits;
		_checkBond = checkBond;
		_getTipHeight = getTipHeight ?? (() => null);
		_lookupPace = new RelayPace(limits.BondLookupBurst, limits.BondLookupsPerSecond);

		store.Reconcile(rules);
	}

	public RelaySenderRules Rules => _rules;

	/// <summary>What the relay tells a key that it does not take events from.</summary>
	public string RefusalMessage =>
		$"restricted: this relay takes events from keys with a fidelity bond of {FormatAmount(_rules.RequiredShare)} or more behind them";

	/// <summary>Tells whether the relay takes an event of the key that signed the given one, and what stands behind that key.</summary>
	/// <param name="now">The time, in seconds since 1970.</param>
	/// <param name="visitorPace">The pace at which the visitor that sent the event may have the node asked about bonds. None for no such pace.</param>
	public async Task<SenderVerdict> AdmitAsync(RelayEvent relayEvent, long now, RelayPace? visitorPace, CancellationToken cancellationToken)
	{
		var publicKey = relayEvent.PublicKey;

		if (_rules.Whitelist.Contains(publicKey))
		{
			return SenderVerdict.Accept(RelaySenderRules.WhitelistRank);
		}

		var known = _store.GetSender(publicKey);
		var isNew = relayEvent.CreatedAt > now - (long)MaxSignAge.TotalSeconds;

		// A bond that has unlocked can be spent, and stands behind nothing any more.
		if (known is not null && _getTipHeight() is { } tipHeight && known.UnlockHeight <= tipHeight)
		{
			Sweep(now);
			known = _store.GetSender(publicKey);
		}

		string? problem = null;

		// A key that shows a bond for the first time, or another bond than the one that the relay knows of it, has it
		// looked up, if it shows it with a new event.
		if (RelayBondClaim.TryRead(relayEvent, out var proof) && known?.Bond != RelayBondClaim.GetBondId(proof.OutPoint))
		{
			(known, problem) = isNew
				? await LookUpAsync(publicKey, proof, known, now, visitorPace, cancellationToken).ConfigureAwait(false)
				: (known, OldProofMessage);
		}

		if (known is not null && known.Share >= _rules.RequiredShare)
		{
			if (isNew)
			{
				_store.Touch(publicKey, now);
			}

			return SenderVerdict.Accept(known.Share);
		}

		return _rules.IsBondRequired
			? SenderVerdict.Refuse(problem ?? RefusalMessage)
			: SenderVerdict.Accept(0, problem);
	}

	/// <summary>
	/// Strikes the keys from the book whose bond no longer stands behind them, see <see cref="RelayEventStore.ForgetSenders"/>.
	/// The relay does that every now and then.
	/// </summary>
	public void Sweep(long now) =>
		_store.ForgetSenders(now, (long)RelayEventSet.Retention.TotalSeconds, _getTipHeight(), dropEvents: _rules.IsBondRequired);

	public void Dispose() => _lookupLock.Dispose();

	/// <summary>Writes an amount of sats in bitcoin, for whoever reads what the relay answers.</summary>
	public static string FormatAmount(long sats) => $"{NostrRelaySettings.FormatBond(sats)} BTC";

	/// <summary>
	/// Has the node look a bond up that a key shows, and enters the key in the book if the chain has the bond and the
	/// bond holds what a key needs.
	/// </summary>
	/// <returns>What the relay knows of the key afterwards, and what to tell the key if that is not enough.</returns>
	private async Task<(SenderBond? Known, string? Problem)> LookUpAsync(string publicKey, BondProof proof, SenderBond? known, long now, RelayPace? visitorPace, CancellationToken cancellationToken)
	{
		if (_checkBond is null)
		{
			return (known, NoLookupMessage);
		}

		if (visitorPace?.TryTake() == false)
		{
			return (known, TooManyLookupsMessage);
		}

		if (!await _lookupLock.WaitAsync(_limits.BondLookupPatience, cancellationToken).ConfigureAwait(false))
		{
			return (known, TooManyLookupsMessage);
		}

		try
		{
			if (!_lookupPace.TryTake())
			{
				return (known, TooManyLookupsMessage);
			}

			var check = await _checkBond(publicKey, proof, cancellationToken).ConfigureAwait(false);

			switch (check.Result)
			{
				case BondCheckResult.Unavailable:
					return (known, NoLookupMessage);

				case BondCheckResult.NotFound:
					return (known, NoSuchBondMessage);
			}

			var bond = RelayBondClaim.GetBondId(proof.OutPoint);

			if (_store.Join(publicKey, bond, check.Amount, proof.UnlockHeight, _rules.RequiredShare, now) is null)
			{
				return (known, $"restricted: the fidelity bond holds {FormatAmount(check.Amount)}, and this relay takes events from keys with {FormatAmount(_rules.RequiredShare)} or more behind them");
			}

			return (_store.GetSender(publicKey), null);
		}
		finally
		{
			_lookupLock.Release();
		}
	}
}
