using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using WalletWasabi.FidelityBonds;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// Whose events a relay takes: with the bond filter, from keys that have proven a fidelity bond of the size that the
/// user set, and from the keys that the user named. A bond that several keys prove is divided evenly among them.
/// </summary>
public sealed class RelaySendersTests : IDisposable
{
	private const long Now = TestRelayEvent.Now;
	private const string NeedsTenthOfACoin = "restricted: this relay takes events from keys with a fidelity bond of 0.1 BTC or more behind them";

	private static readonly RelayLimits Limits = RelayLimits.ForCustomEvents;

	private readonly TestBondNode _node = new();
	private readonly RelayEventStore _store = RelayEventStore.Open(filePath: null, Limits);
	private readonly List<ECPrivKey> _keys = [];

	public void Dispose()
	{
		_store.Dispose();
		_node.Dispose();

		foreach (var key in _keys)
		{
			key.Dispose();
		}
	}

	private ECPrivKey NewKey()
	{
		var key = TestRelayEvent.NewKey();
		_keys.Add(key);
		return key;
	}

	private RelaySenders CreateSenders(RelaySenderRules rules, RelayLimits? limits = null, bool hasNode = true) =>
		new(rules, _store, limits ?? Limits, hasNode ? _node.CheckAsync : null, () => _node.TipHeight);

	/// <summary>An event of the key, which shows the bond if one is given.</summary>
	private static RelayEvent Note(ECPrivKey key, TestBond? bond = null, string text = "a note", long createdAt = Now) =>
		TestRelayEvent.Create(key, 1, text, bond is null ? [] : [bond.Tag(key)], createdAt);

	private static Task<SenderVerdict> AdmitAsync(RelaySenders senders, RelayEvent relayEvent, long now = Now, RelayPace? visitorPace = null) =>
		senders.AdmitAsync(relayEvent, now, visitorPace, TestContext.Current.CancellationToken);

	/// <summary>Without the filter the relay takes events from anybody. A bond that a key shows still stands behind its events.</summary>
	[Fact]
	public async Task AnybodyIsTakenWithoutTheFilterAsync()
	{
		using var senders = CreateSenders(RelaySenderRules.Anyone);
		var plain = NewKey();
		var bonded = NewKey();
		var liar = NewKey();

		Assert.Equal(SenderVerdict.Accept(0), await AdmitAsync(senders, Note(plain)));
		Assert.Equal(0, _node.Lookups);

		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(bonded, _node.Lock(30_000_000))));
		Assert.Equal(1, _node.Lookups);

		// A bond that the chain does not have stands behind nothing, and its key is taken like anybody. The relay
		// keeps in mind why the bond did not count, for the case that it has no room for the event.
		Assert.Equal(SenderVerdict.Accept(0, RelaySenders.NoSuchBondMessage), await AdmitAsync(senders, Note(liar, _node.Invent(900_000_000))));
		Assert.Null(_store.GetSender(TestRelayEvent.GetPublicKey(liar)));

		// So is a key whose bond cannot be looked up.
		_node.IsAvailable = false;
		Assert.Equal(SenderVerdict.Accept(0, RelaySenders.NoLookupMessage), await AdmitAsync(senders, Note(NewKey(), _node.Lock(5_000_000))));
	}

	/// <summary>With the filter, a key needs a bond of the minimum or more. It shows it once: after that the bond stands behind every event of the key.</summary>
	[Fact]
	public async Task KeyNeedsABondOfTheMinimumWithTheFilterAsync()
	{
		using var senders = CreateSenders(new RelaySenderRules(IsBondRequired: true, MinBond: 10_000_000, Whitelist: []));
		var bonded = NewKey();
		var exact = NewKey();
		var small = NewKey();
		var plain = NewKey();
		var liar = NewKey();

		Assert.Equal(NeedsTenthOfACoin, senders.RefusalMessage);
		Assert.Equal(SenderVerdict.Refuse(NeedsTenthOfACoin), await AdmitAsync(senders, Note(plain)));
		Assert.Equal(0, _node.Lookups);

		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(bonded, _node.Lock(30_000_000))));
		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(bonded, text: "without the proof")));
		Assert.Equal(1, _node.Lookups);

		// A bond of exactly the minimum is enough, and one sat less is not.
		Assert.Equal(SenderVerdict.Accept(10_000_000), await AdmitAsync(senders, Note(exact, _node.Lock(10_000_000))));
		Assert.Equal(
			SenderVerdict.Refuse("restricted: the fidelity bond holds 0.09999999 BTC, and this relay takes events from keys with 0.1 BTC or more behind them"),
			await AdmitAsync(senders, Note(small, _node.Lock(9_999_999))));
		Assert.Null(_store.GetSender(TestRelayEvent.GetPublicKey(small)));

		// A bond that the chain does not have.
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.NoSuchBondMessage), await AdmitAsync(senders, Note(liar, _node.Invent(900_000_000))));

		// While the node cannot be asked, a new key is told so, and the keys that the relay knows are served as before.
		_node.IsAvailable = false;
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.NoLookupMessage), await AdmitAsync(senders, Note(NewKey(), _node.Lock(50_000_000))));
		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(bonded, text: "while the node is away")));
		Assert.Equal(SenderVerdict.Refuse(NeedsTenthOfACoin), await AdmitAsync(senders, Note(plain)));
	}

	/// <summary>
	/// The keys that prove the same bond share it evenly, and each share has to reach the minimum: a bond of 3 BTC
	/// stands behind three keys with 1 BTC each. Spreading it over more keys gains nothing: the bond stands behind
	/// the keys that showed it last.
	/// </summary>
	[Fact]
	public async Task KeysThatProveTheSameBondShareItAsync()
	{
		using var senders = CreateSenders(new RelaySenderRules(IsBondRequired: true, MinBond: 100_000_000, Whitelist: []));
		var bond = _node.Lock(300_000_000);
		var keys = Enumerable.Range(0, 5).Select(_ => NewKey()).ToArray();
		var names = keys.Select(TestRelayEvent.GetPublicKey).ToArray();

		Assert.Equal(SenderVerdict.Accept(300_000_000), await AdmitAsync(senders, Note(keys[0], bond), Now));
		Assert.Equal(SenderVerdict.Accept(150_000_000), await AdmitAsync(senders, Note(keys[1], bond), Now + 1));
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(keys[2], bond), Now + 2));

		// Each of the three has a third now.
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(keys[0], text: "again"), Now + 3));
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(keys[1], bond, "with the proof again"), Now + 4));
		Assert.Equal(3, _node.Lookups);

		// A fourth key takes the place of the one that the relay heard of the longest ago: the third.
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(keys[3], bond), Now + 5));
		Assert.Null(_store.GetSender(names[2]));
		Assert.Equal(SenderVerdict.Refuse(senders.RefusalMessage), await AdmitAsync(senders, Note(keys[2], text: "without the proof"), Now + 6));

		// With the proof it is back, in the place of the first.
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(keys[2], bond), Now + 7));
		Assert.Null(_store.GetSender(names[0]));
		Assert.Equal([names[1], names[2], names[3]], names.Where(name => _store.GetSender(name) is not null));

		// Another bond is another matter.
		Assert.Equal(SenderVerdict.Accept(120_000_000), await AdmitAsync(senders, Note(keys[4], _node.Lock(120_000_000)), Now + 8));
		Assert.Equal(100_000_000, _store.GetSender(names[1])!.Share);
	}

	/// <summary>
	/// Events are public, and anybody can send an old event of somebody else again. Only a new event is a sign of its
	/// key: an old one does not show a bond, and does not count as the relay hearing of the key. So the keys that a
	/// maker used before cannot be brought back to take the place of the ones that it uses now.
	/// </summary>
	[Fact]
	public async Task OldEventIsNoSignOfItsKeyAsync()
	{
		using var senders = CreateSenders(new RelaySenderRules(IsBondRequired: true, MinBond: 100_000_000, Whitelist: []));
		var bond = _node.Lock(200_000_000);
		var before = NewKey();
		var current = NewKey();
		var other = NewKey();
		var next = NewKey();
		const long Later = Now + 20 * 60;

		// The maker used one key, and uses two others twenty minutes later. The bond holds two shares.
		var oldOffer = Note(before, bond, "the offer of before", createdAt: Now);
		Assert.Equal(SenderVerdict.Accept(200_000_000), await AdmitAsync(senders, oldOffer, Now));
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(current, bond, createdAt: Later), Later));
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(other, bond, createdAt: Later + 60), Later + 60));
		Assert.Null(_store.GetSender(TestRelayEvent.GetPublicKey(before)));
		Assert.Equal(3, _node.Lookups);

		// Somebody sends the offer of before again. It is twenty minutes old, shows no bond, and the node is not asked.
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.OldProofMessage), await AdmitAsync(senders, oldOffer, Later + 120));
		Assert.Null(_store.GetSender(TestRelayEvent.GetPublicKey(before)));
		Assert.NotNull(_store.GetSender(TestRelayEvent.GetPublicKey(current)));
		Assert.Equal(3, _node.Lookups);

		// An event of fifteen minutes ago is too old, and one of a second less is not.
		var fresh = NewKey();
		Assert.Equal(
			SenderVerdict.Refuse(RelaySenders.OldProofMessage),
			await AdmitAsync(senders, Note(fresh, _node.Lock(500_000_000), createdAt: Later - 15 * 60), Later));
		Assert.Equal(
			SenderVerdict.Accept(500_000_000),
			await AdmitAsync(senders, Note(fresh, _node.Lock(500_000_000), "another", createdAt: Later - 15 * 60 + 1), Later));

		// An old event of a key that the relay knows is taken, and is no sign of that key: when the maker uses a third
		// key, the bond leaves the key that was really heard of the longest ago, which is the first of the two.
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(current, text: "an old note", createdAt: Now), Later + 180));
		Assert.Equal(SenderVerdict.Accept(100_000_000), await AdmitAsync(senders, Note(next, bond, createdAt: Later + 240), Later + 240));
		Assert.Null(_store.GetSender(TestRelayEvent.GetPublicKey(current)));
		Assert.NotNull(_store.GetSender(TestRelayEvent.GetPublicKey(other)));

		// Without the filter an old event with a proof is taken like an event without a bond.
		using var open = CreateSenders(RelaySenderRules.Anyone);
		Assert.Equal(
			SenderVerdict.Accept(0, RelaySenders.OldProofMessage),
			await AdmitAsync(open, Note(NewKey(), _node.Lock(300_000_000), createdAt: Now), Later));
	}

	/// <summary>The keys that the user named need no bond, and the node is not asked about them.</summary>
	[Fact]
	public async Task KeyOfTheWhitelistNeedsNoBondAsync()
	{
		var listed = NewKey();
		var other = NewKey();
		var rules = new RelaySenderRules(IsBondRequired: true, MinBond: 2_100_000_000_000_000, Whitelist: [TestRelayEvent.GetPublicKey(listed)]);

		using (var senders = CreateSenders(rules))
		{
			Assert.Equal(SenderVerdict.Accept(RelaySenderRules.WhitelistRank), await AdmitAsync(senders, Note(listed)));
			Assert.Equal(SenderVerdict.Accept(RelaySenderRules.WhitelistRank), await AdmitAsync(senders, Note(listed, _node.Lock(1_000))));
			Assert.Equal(0, _node.Lookups);
			Assert.Null(_store.GetSender(TestRelayEvent.GetPublicKey(listed)));

			Assert.False((await AdmitAsync(senders, Note(other))).IsAccepted);
			Assert.False((await AdmitAsync(senders, Note(other, _node.Lock(2_000_000_000)))).IsAccepted);
		}

		// Without the filter the list still says whose events are the last to make way.
		using (var senders = CreateSenders(rules with { IsBondRequired = false }))
		{
			Assert.Equal(SenderVerdict.Accept(RelaySenderRules.WhitelistRank), await AdmitAsync(senders, Note(listed)));
			Assert.Equal(SenderVerdict.Accept(0), await AdmitAsync(senders, Note(other, text: "another note")));
		}
	}

	/// <summary>A bond that has unlocked can be spent, so it stands behind nothing any more.</summary>
	[Fact]
	public async Task BondThatUnlockedStandsBehindNothingAsync()
	{
		using var senders = CreateSenders(new RelaySenderRules(IsBondRequired: true, MinBond: 1_000_000, Whitelist: []));
		var key = NewKey();
		var bond = _node.Lock(30_000_000, unlockHeight: TestBondNode.Tip + 10);

		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(key, bond)));

		_node.TipHeight = TestBondNode.Tip + 9;
		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(key, text: "a block before")));

		_node.TipHeight = TestBondNode.Tip + 10;
		Assert.Equal(SenderVerdict.Refuse(senders.RefusalMessage), await AdmitAsync(senders, Note(key, text: "at the height")));
		Assert.Null(_store.GetSender(TestRelayEvent.GetPublicKey(key)));
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.NoSuchBondMessage), await AdmitAsync(senders, Note(key, bond, "with the proof")));
	}

	/// <summary>A bond stands behind a key for as long as the relay keeps events of it: a day after the last one.</summary>
	[Fact]
	public async Task BondStandsBehindAKeyForADayAfterItsLastEventAsync()
	{
		using var senders = CreateSenders(new RelaySenderRules(IsBondRequired: true, MinBond: 1_000_000, Whitelist: []));
		var key = NewKey();
		var name = TestRelayEvent.GetPublicKey(key);

		Assert.True((await AdmitAsync(senders, Note(key, _node.Lock(30_000_000)), Now)).IsAccepted);
		Assert.True((await AdmitAsync(senders, Note(key, text: "an hour later", createdAt: Now + 3_600), Now + 3_600)).IsAccepted);

		senders.Sweep(Now + 3_600 + 86_400 - 1);
		Assert.NotNull(_store.GetSender(name));

		senders.Sweep(Now + 3_600 + 86_400);
		Assert.Null(_store.GetSender(name));
		Assert.Equal(SenderVerdict.Refuse(senders.RefusalMessage), await AdmitAsync(senders, Note(key, text: "a day later", createdAt: Now + 3_600 + 86_400), Now + 3_600 + 86_400));
	}

	/// <summary>A key that shows another bond than the one that the relay knows of it moves to that bond, if the chain has it.</summary>
	[Fact]
	public async Task KeyThatShowsAnotherBondMovesToItAsync()
	{
		using var senders = CreateSenders(RelaySenderRules.Anyone);
		var key = NewKey();
		var companion = NewKey();
		var first = _node.Lock(30_000_000);
		var second = _node.Lock(50_000_000);

		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(key, first)));
		Assert.Equal(SenderVerdict.Accept(15_000_000), await AdmitAsync(senders, Note(companion, first)));

		Assert.Equal(SenderVerdict.Accept(50_000_000), await AdmitAsync(senders, Note(key, second, "the other bond")));
		Assert.Equal(second.Id, _store.GetSender(TestRelayEvent.GetPublicKey(key))!.Bond);
		Assert.Equal(30_000_000, _store.GetSender(TestRelayEvent.GetPublicKey(companion))!.Share);

		// A bond that the chain does not have changes nothing.
		Assert.True((await AdmitAsync(senders, Note(key, _node.Invent(1), "a bond that is none"))) is { IsAccepted: true, Rank: 50_000_000 });
	}

	/// <summary>
	/// Asking the node is work that a stranger can make the relay do, so it has a pace: for all visitors together, and
	/// for each of them. Beyond it a key that shows a new bond is told to come back, and the node is not asked.
	/// </summary>
	[Fact]
	public async Task NodeIsAskedAtAPaceAsync()
	{
		using var senders = CreateSenders(
			new RelaySenderRules(IsBondRequired: true, MinBond: 1_000_000, Whitelist: []),
			Limits with { BondLookupBurst = 3, BondLookupsPerSecond = 0 });
		var known = NewKey();

		Assert.True((await AdmitAsync(senders, Note(known, _node.Lock(30_000_000)))).IsAccepted);

		// One visitor that has used up its own turns does not use up the turns of the others.
		var visitorPace = new RelayPace(burst: 1, perSecond: 0);
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.NoSuchBondMessage), await AdmitAsync(senders, Note(NewKey(), _node.Invent(1)), visitorPace: visitorPace));
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.TooManyLookupsMessage), await AdmitAsync(senders, Note(NewKey(), _node.Lock(30_000_000)), visitorPace: visitorPace));
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.TooManyLookupsMessage), await AdmitAsync(senders, Note(NewKey(), _node.Lock(30_000_000)), visitorPace: visitorPace));
		Assert.Equal(2, _node.Lookups);

		// The third turn of all visitors together is the last.
		Assert.True((await AdmitAsync(senders, Note(NewKey(), _node.Lock(30_000_000)))).IsAccepted);
		Assert.Equal(SenderVerdict.Refuse(RelaySenders.TooManyLookupsMessage), await AdmitAsync(senders, Note(NewKey(), _node.Lock(30_000_000))));
		Assert.Equal(3, _node.Lookups);

		// The keys that the relay knows need no turn.
		Assert.Equal(SenderVerdict.Accept(30_000_000), await AdmitAsync(senders, Note(known, text: "another note")));
	}

	/// <summary>The node is asked about one bond at a time. An event does not wait for its turn longer than the relay has patience.</summary>
	[Fact(Timeout = 60000)]
	public async Task EventDoesNotWaitLongForTheNodeAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var answer = new TaskCompletionSource<BondCheck>(TaskCreationOptions.RunContinuationsAsynchronously);

		using var senders = new RelaySenders(
			new RelaySenderRules(IsBondRequired: true, MinBond: 1_000_000, Whitelist: []),
			_store,
			Limits with { BondLookupPatience = TimeSpan.FromMilliseconds(50) },
			(_, _, _) =>
			{
				asked.TrySetResult();
				return answer.Task;
			});

		var first = AdmitAsync(senders, Note(NewKey(), _node.Lock(30_000_000)));
		await asked.Task.WaitAsync(cancellationToken);

		Assert.Equal(SenderVerdict.Refuse(RelaySenders.TooManyLookupsMessage), await AdmitAsync(senders, Note(NewKey(), _node.Lock(30_000_000))));

		answer.SetResult(new BondCheck(BondCheckResult.Found, 30_000_000));
		Assert.Equal(SenderVerdict.Accept(30_000_000), await first);
	}

	/// <summary>A relay without a node cannot look a bond up: with the filter, only the keys of the list are taken.</summary>
	[Fact]
	public async Task WithoutANodeNoBondCountsAsync()
	{
		var key = NewKey();
		var bond = _node.Lock(30_000_000);

		using (var senders = CreateSenders(new RelaySenderRules(IsBondRequired: true, MinBond: 1, Whitelist: []), hasNode: false))
		{
			Assert.Equal(SenderVerdict.Refuse(RelaySenders.NoLookupMessage), await AdmitAsync(senders, Note(key, bond)));
			Assert.Equal(SenderVerdict.Refuse("restricted: this relay takes events from keys with a fidelity bond of 0.00000001 BTC or more behind them"), await AdmitAsync(senders, Note(key)));
		}

		using (var senders = CreateSenders(RelaySenderRules.Anyone, hasNode: false))
		{
			Assert.Equal(SenderVerdict.Accept(0, RelaySenders.NoLookupMessage), await AdmitAsync(senders, Note(key, bond)));
		}

		Assert.Equal(0, _node.Lookups);
	}

	/// <summary>
	/// The node of the user is asked the way the order book asks it. A node that cannot be reached says nothing about
	/// a bond, and is left alone for a while after it could not be asked.
	/// </summary>
	[Fact(Timeout = 60000)]
	public async Task NodeTellsWhetherTheChainHasABondAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var rpc = new MockRpcClient();
		var outputs = new Dictionary<OutPoint, GetTxOutResponse>();
		var tipQuestions = 0;
		var isReachable = true;
		long clock = 0;

		rpc.OnGetBlockCountAsync = () =>
		{
			tipQuestions++;
			return isReachable ? Task.FromResult((int)TestBondNode.Tip) : throw new HttpRequestException("Connection refused");
		};
		rpc.OnGetTxOutAsync = (txid, index, _) => outputs.GetValueOrDefault(new OutPoint(txid, index));

		using var checker = new NodeBondChecker(rpc, () => clock);
		var key = NewKey();
		var name = TestRelayEvent.GetPublicKey(key);
		var bond = _node.Lock(30_000_000);
		var missing = _node.Invent(30_000_000);

		outputs[bond.OutPoint] = new GetTxOutResponse
		{
			Confirmations = 6,
			TxOut = new TxOut(Money.Satoshis(bond.Amount), BondScript.GetScriptPubKey(BondScript.CreateLeaf(bond.Key.PubKey, bond.UnlockHeight)))
		};

		Assert.Equal(new BondCheck(BondCheckResult.Found, 30_000_000), await checker.CheckAsync(name, bond.Prove(key), cancellationToken));
		Assert.Equal(new BondCheck(BondCheckResult.NotFound), await checker.CheckAsync(name, missing.Prove(key), cancellationToken));

		// The proof of another key is no proof for this one.
		Assert.Equal(new BondCheck(BondCheckResult.NotFound), await checker.CheckAsync(name, bond.Prove(NewKey()), cancellationToken));
		Assert.Equal(3, tipQuestions);

		// The node is away.
		isReachable = false;
		Assert.Equal(new BondCheck(BondCheckResult.Unavailable), await checker.CheckAsync(name, bond.Prove(key), cancellationToken));
		Assert.Equal(4, tipQuestions);

		// It is back, and is left alone for half a minute all the same.
		isReachable = true;
		clock = 29_999;
		Assert.Equal(new BondCheck(BondCheckResult.Unavailable), await checker.CheckAsync(name, bond.Prove(key), cancellationToken));
		Assert.Equal(4, tipQuestions);

		clock = 30_000;
		Assert.Equal(new BondCheck(BondCheckResult.Found, 30_000_000), await checker.CheckAsync(name, bond.Prove(key), cancellationToken));
		Assert.Equal(5, tipQuestions);
	}

	/// <summary>A node that does not answer in time is a node that cannot be asked.</summary>
	[Fact(Timeout = 60000)]
	public async Task NodeThatTakesTooLongCannotBeAskedAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var rpc = new MockRpcClient();
		var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		rpc.OnGetBlockCountAsync = () => never.Task;

		using var checker = new NodeBondChecker(rpc, answerTimeout: TimeSpan.FromMilliseconds(100));
		var key = NewKey();

		Assert.Equal(
			new BondCheck(BondCheckResult.Unavailable),
			await checker.CheckAsync(TestRelayEvent.GetPublicKey(key), _node.Lock(30_000_000).Prove(key), cancellationToken));

		never.SetResult(0);
	}
}
