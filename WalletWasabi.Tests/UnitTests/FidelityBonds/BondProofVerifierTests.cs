using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.RPC;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The fidelity bond of another maker counts when the node has it: an output in a block, unspent, with the script
/// of the proof, still locked, and signed for by its key.
/// </summary>
public class BondProofVerifierTests
{
	private const uint Tip = 800_000;

	private readonly MockRpcClient _rpc = new();
	private readonly Dictionary<OutPoint, GetTxOutResponse> _outputs = [];
	private readonly List<(OutPoint OutPoint, bool IncludeMempool)> _asked = [];
	private readonly BondProofVerifier _verifier;
	private int _tip = (int)Tip;
	private int _tipQuestions;

	public BondProofVerifierTests()
	{
		_rpc.OnGetBlockCountAsync = () =>
		{
			_tipQuestions++;
			return Task.FromResult(_tip);
		};
		_rpc.OnGetTxOutAsync = (txid, index, includeMempool) =>
		{
			var outPoint = new OutPoint(txid, index);
			_asked.Add((outPoint, includeMempool));
			return _outputs.GetValueOrDefault(outPoint);
		};
		_verifier = new BondProofVerifier(_rpc);
	}

	[Fact]
	public async Task BondThatTheChainHasCountsWithWhatItHoldsAsync()
	{
		var claim = AddBond(30_000_000);

		await AssertAmountsAsync([30_000_000L], claim);

		// What is only in the mempool is not looked at: it can be taken back.
		Assert.Equal([(claim.Proof.OutPoint, false)], _asked);
	}

	[Fact]
	public async Task EveryClaimGetsItsOwnAnswerAsync()
	{
		var first = AddBond(1_000_000);
		var missing = AddBond(2_000_000);
		var third = AddBond(3_000_000);
		_outputs.Remove(missing.Proof.OutPoint);

		await AssertAmountsAsync([1_000_000L, 0L, 3_000_000L], first, missing, third);
	}

	[Fact]
	public async Task WithoutClaimsTheNodeIsNotAskedAsync()
	{
		Assert.Empty(await _verifier.VerifyAsync([], CancellationToken.None));
		Assert.Equal(0, _tipQuestions);
		Assert.Empty(_asked);
	}

	[Fact]
	public async Task BondThatNoBlockHasYetDoesNotCountAsync()
	{
		var claim = AddBond(30_000_000, confirmations: 0);

		await AssertAmountsAsync([0L], claim);

		// Once it is in a block it counts.
		_outputs[claim.Proof.OutPoint].Confirmations = 1;
		await AssertAmountsAsync([30_000_000L], claim);
	}

	[Fact]
	public async Task OutputThatIsSpentOrWasNeverThereDoesNotCountAsync()
	{
		var claim = AddBond(30_000_000);
		_outputs.Clear();

		await AssertAmountsAsync([0L], claim);
	}

	/// <summary>Whoever names a coin of somebody else, or one that is locked for a shorter time than told, names no bond.</summary>
	[Fact]
	public async Task OutputWithAnotherScriptIsNotTheBondOfTheProofAsync()
	{
		using var bondKey = new Key();
		using var otherKey = new Key();
		var offerKey = CreateOfferKey();
		var outPoint = new OutPoint(RandomUtils.GetUInt256(), 0);

		// The output is a bond that unlocks ten blocks from now. The proof tells of a year.
		var realScript = BondScript.GetScriptPubKey(BondScript.CreateLeaf(bondKey.PubKey, Tip + 10));
		_outputs[outPoint] = Output(30_000_000, realScript);

		var longer = BondProof.Create(bondKey, outPoint, Tip + 52_560, offerKey);
		await AssertAmountsAsync([0L], (Hex(offerKey), longer));

		// The output is somebody else's.
		var foreign = BondProof.Create(otherKey, outPoint, Tip + 10, offerKey);
		await AssertAmountsAsync([0L], (Hex(offerKey), foreign));

		// The proof that tells it as it is.
		var honest = BondProof.Create(bondKey, outPoint, Tip + 10, offerKey);
		await AssertAmountsAsync([30_000_000L], (Hex(offerKey), honest));
	}

	[Fact]
	public async Task BondThatHasUnlockedIsNoBondAnyMoreAsync()
	{
		var atTip = AddBond(30_000_000, unlockHeight: Tip);
		var past = AddBond(30_000_000, unlockHeight: Tip - 1);
		var next = AddBond(30_000_000, unlockHeight: Tip + 1);

		await AssertAmountsAsync([0L, 0L, 30_000_000L], atTip, past, next);

		// The node is not asked about what cannot count.
		Assert.Equal([(next.Proof.OutPoint, false)], _asked);
	}

	[Fact]
	public async Task ProofThatWasMadeForAnotherOfferDoesNotCountAsync()
	{
		var claim = AddBond(30_000_000);
		var thief = Hex(CreateOfferKey());

		await AssertAmountsAsync([0L], (thief, claim.Proof));
		Assert.Empty(_asked);
	}

	[Fact]
	public async Task OfferWithoutAKeyHasNoBondAsync()
	{
		var claim = AddBond(30_000_000);

		foreach (var offerKey in new[] { "", "abcd", new string('z', 64), claim.OfferKey + "00" })
		{
			await AssertAmountsAsync([0L], (offerKey, claim.Proof));
		}

		Assert.Empty(_asked);
	}

	[Fact]
	public async Task ProofThatIsNotWellFormedDoesNotCountAsync()
	{
		var claim = AddBond(30_000_000);

		long[] amounts = await _verifier.VerifyAsync(
			[
				(claim.OfferKey, claim.Proof with { Signature = "" }),
				(claim.OfferKey, claim.Proof with { Signature = new string('0', 128) }),
				(claim.OfferKey, claim.Proof with { Key = "ab" }),
				(claim.OfferKey, claim.Proof with { UnlockHeight = BondScript.MaxUnlockHeight + 1 })
			],
			CancellationToken.None);

		Assert.Equal([0L, 0L, 0L, 0L], amounts);
		Assert.Empty(_asked);
	}

	/// <summary>
	/// A bond cannot be spent before it unlocks, so the node is asked about it once. When the chain reaches the unlock
	/// height the bond is over.
	/// </summary>
	[Fact]
	public async Task BondIsRememberedUntilItUnlocksAsync()
	{
		var claim = AddBond(30_000_000, unlockHeight: Tip + 100);

		await AssertAmountsAsync([30_000_000L], claim);
		await AssertAmountsAsync([30_000_000L], claim);
		Assert.Single(_asked);

		_tip = (int)Tip + 99;
		await AssertAmountsAsync([30_000_000L], claim);

		_tip = (int)Tip + 100;
		await AssertAmountsAsync([0L], claim);
		Assert.Single(_asked);
	}

	/// <summary>An output that is not a bond can be spent at any time: the node is asked about it every time.</summary>
	[Fact]
	public async Task OutputThatIsNoBondIsNotRememberedAsync()
	{
		var claim = AddBond(30_000_000, confirmations: 0);

		await _verifier.VerifyAsync([claim], CancellationToken.None);
		await _verifier.VerifyAsync([claim], CancellationToken.None);

		Assert.Equal(2, _asked.Count);
	}

	/// <summary>Anybody can name the bond of somebody else. That does not take the bond from the one who has its key.</summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task FalseClaimDoesNotStandInTheWayOfTheTrueOneAsync(bool falseClaimFirst)
	{
		using var otherKey = new Key();
		var honest = AddBond(30_000_000);
		var liarKey = CreateOfferKey();
		var liar = (Hex(liarKey), BondProof.Create(otherKey, honest.Proof.OutPoint, honest.Proof.UnlockHeight, liarKey));

		(string, BondProof)[] claims = falseClaimFirst ? [liar, honest] : [honest, liar];
		long[] expected = falseClaimFirst ? [0L, 30_000_000L] : [30_000_000L, 0L];

		Assert.Equal(expected, await _verifier.VerifyAsync(claims, CancellationToken.None));

		// And when the false claim came alone before.
		var other = AddBond(5_000_000);
		var otherLiar = (Hex(liarKey), BondProof.Create(otherKey, other.Proof.OutPoint, other.Proof.UnlockHeight, liarKey));

		await AssertAmountsAsync([0L], otherLiar);
		await AssertAmountsAsync([5_000_000L], other);
		await AssertAmountsAsync([0L], otherLiar);
	}

	/// <summary>A wallet shows the same bond with every offer that it has.</summary>
	[Fact]
	public async Task OneBondStandsBehindSeveralOffersOfItsOwnerAsync()
	{
		using var bondKey = new Key();
		var outPoint = new OutPoint(RandomUtils.GetUInt256(), 1);
		var unlockHeight = Tip + 1_000;
		var firstOffer = CreateOfferKey();
		var secondOffer = CreateOfferKey();
		var first = BondProof.Create(bondKey, outPoint, unlockHeight, firstOffer);
		var second = BondProof.Create(bondKey, outPoint, unlockHeight, secondOffer);
		_outputs[outPoint] = Output(7_000_000, first.GetScriptPubKey());

		long[] amounts = await _verifier.VerifyAsync([(Hex(firstOffer), first), (Hex(secondOffer), second)], CancellationToken.None);

		Assert.Equal([7_000_000L, 7_000_000L], amounts);
		Assert.Single(_asked);
	}

	[Fact]
	public async Task BookFullOfBondsIsAskedAboutInBatchesAsync()
	{
		var claims = Enumerable.Range(1, 2 * BondProofVerifier.BatchSize + 20).Select(i => AddBond(i * 1_000L)).ToArray();

		var amounts = await _verifier.VerifyAsync(claims, CancellationToken.None);

		Assert.Equal(claims.Select((_, i) => (i + 1) * 1_000L), amounts);
		Assert.Equal(claims.Length, _asked.Count);
		Assert.Equal(1, _tipQuestions);
	}

	[Fact]
	public async Task NodeThatDoesNotAnswerIsNoAnswerAsync()
	{
		var claim = AddBond(30_000_000);
		_rpc.OnGetBlockCountAsync = () => Task.FromException<int>(new System.Net.Http.HttpRequestException("Connection refused"));

		await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => _verifier.VerifyAsync([claim], CancellationToken.None));
	}

	/// <summary>Asks about the claims, and checks what each of them was found to hold.</summary>
	private async Task AssertAmountsAsync(long[] expected, params (string OfferKey, BondProof Proof)[] claims)
	{
		var amounts = await _verifier.VerifyAsync(claims, CancellationToken.None);

		Assert.Equal(expected, amounts);
	}

	private (string OfferKey, BondProof Proof) AddBond(long amount, uint unlockHeight = Tip + 1_000, int confirmations = 6)
	{
		using var bondKey = new Key();
		var offerKey = CreateOfferKey();
		var outPoint = new OutPoint(RandomUtils.GetUInt256(), 1);
		var proof = BondProof.Create(bondKey, outPoint, unlockHeight, offerKey);

		_outputs[outPoint] = Output(amount, proof.GetScriptPubKey(), confirmations);
		return (Hex(offerKey), proof);
	}

	private static GetTxOutResponse Output(long amount, Script scriptPubKey, int confirmations = 6) =>
		new() { Confirmations = confirmations, TxOut = new TxOut(Money.Satoshis(amount), scriptPubKey) };

	private static byte[] CreateOfferKey()
	{
		using var key = new Key();
		return key.PubKey.TaprootInternalKey.ToBytes();
	}

	private static string Hex(byte[] bytes) => Encoders.Hex.EncodeData(bytes);
}
