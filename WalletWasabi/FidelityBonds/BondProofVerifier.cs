using System.Threading;
using System.Threading.Tasks;
using NBitcoin.DataEncoders;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// Checks the proofs of fidelity bonds that offers carry, with the help of the node: a bond counts if the chain has
/// the unspent output that the proof names, with the script that the proof tells, if that output is still locked, and
/// if its key signed the key of the offer.
/// </summary>
/// <remarks>
/// A bond cannot be spent before its unlock height. So an output that was found once is not looked up again until
/// that height: the node is asked about a bond one time, however often the order book is read.
/// </remarks>
public class BondProofVerifier
{
	/// <summary>How many outputs the node is asked about in one go.</summary>
	public const int BatchSize = 50;

	/// <summary>How many bonds are remembered at most.</summary>
	private const int MaxRemembered = 5_000;

	private readonly IRPCClient _rpcClient;

	/// <summary>
	/// The bonds that the node confirmed: outputs with the script of a proof. The script and the amount of the output
	/// are kept, and the height that it is locked until.
	/// </summary>
	private readonly Dictionary<OutPoint, (Script ScriptPubKey, long Amount, uint UnlockHeight)> _confirmed = [];

	public BondProofVerifier(IRPCClient rpcClient)
	{
		_rpcClient = rpcClient;
	}

	/// <summary>
	/// Tells what the bond of every claim holds, in sats: nothing if the proof does not hold, or if the node could not
	/// say.
	/// </summary>
	/// <param name="claims">The key that signed an offer, in hex, with the proof that the offer carries.</param>
	/// <returns>The amounts in the order of the claims.</returns>
	/// <remarks>The method is not thread safe: one check at a time.</remarks>
	public async Task<long[]> VerifyAsync(IReadOnlyList<(string OfferKey, BondProof Proof)> claims, CancellationToken cancellationToken)
	{
		var amounts = new long[claims.Count];

		if (claims.Count == 0)
		{
			return amounts;
		}

		var tipHeight = (uint)await _rpcClient.GetBlockCountAsync(cancellationToken).ConfigureAwait(false);

		// What has unlocked can be spent, and is no bond any more.
		foreach (var unlocked in _confirmed.Where(bond => bond.Value.UnlockHeight <= tipHeight).Select(bond => bond.Key).ToArray())
		{
			_confirmed.Remove(unlocked);
		}

		if (_confirmed.Count > MaxRemembered)
		{
			_confirmed.Clear();
		}

		// What can be told without the node comes first: a signature that does not hold, or a lock that is over.
		var scripts = new Script?[claims.Count];

		for (var i = 0; i < claims.Count; i++)
		{
			var (offerKey, proof) = claims[i];

			if (proof.UnlockHeight > tipHeight && TryDecodeKey(offerKey) is { } key && proof.IsSignedFor(key))
			{
				scripts[i] = proof.GetScriptPubKey();
			}
		}

		var toAskAbout = Enumerable.Range(0, claims.Count)
			.Where(i => scripts[i] is not null && !_confirmed.ContainsKey(claims[i].Proof.OutPoint))
			.Select(i => claims[i].Proof)
			.DistinctBy(proof => proof.OutPoint)
			.ToArray();

		// What the node tells about the outputs that the proofs name. An output is only remembered beyond this check
		// once it has turned out to be a bond: anything else can be spent at any time.
		var seen = new Dictionary<OutPoint, (Script ScriptPubKey, long Amount)>();

		foreach (var proofs in toAskAbout.Chunk(BatchSize))
		{
			var batch = _rpcClient.PrepareBatch();
			var requests = proofs
				.Select(proof => (Proof: proof, Request: batch.GetTxOutAsync(proof.OutPoint.Hash, (int)proof.OutPoint.N, includeMempool: false, cancellationToken)))
				.ToArray();

			await batch.SendBatchAsync(cancellationToken).ConfigureAwait(false);

			foreach (var (proof, request) in requests)
			{
				GetTxOutResponse? output;

				try
				{
					output = await request.ConfigureAwait(false);
				}
				catch (RPCException)
				{
					// A request that the node refuses is a request without an answer. A node that cannot be reached is another matter.
					continue;
				}

				// The output is in a block and unspent. Whether it is the bond that a proof tells is seen from its script.
				if (output is { Confirmations: > 0, TxOut: { } txOut })
				{
					seen[proof.OutPoint] = (txOut.ScriptPubKey, txOut.Value.Satoshi);
				}
			}
		}

		for (var i = 0; i < claims.Count; i++)
		{
			var proof = claims[i].Proof;

			if (scripts[i] is not { } script)
			{
				continue;
			}

			// The script commits to the unlock height and to the key: an output with the script of the proof is the bond that the proof tells.
			if (_confirmed.TryGetValue(proof.OutPoint, out var bond))
			{
				amounts[i] = bond.ScriptPubKey == script ? bond.Amount : 0;
			}
			else if (seen.TryGetValue(proof.OutPoint, out var output) && output.ScriptPubKey == script)
			{
				amounts[i] = output.Amount;
				_confirmed[proof.OutPoint] = (output.ScriptPubKey, output.Amount, proof.UnlockHeight);
			}
		}

		return amounts;
	}

	private static byte[]? TryDecodeKey(string hex)
	{
		if (hex.Length != 64)
		{
			return null;
		}

		try
		{
			return Encoders.Hex.DecodeData(hex);
		}
		catch (FormatException)
		{
			return null;
		}
	}
}
