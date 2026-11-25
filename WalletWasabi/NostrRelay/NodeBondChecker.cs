using WalletWasabi.BitcoinRpc;
using WalletWasabi.FidelityBonds;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// Has the user's node look the fidelity bonds up that keys show to the relay: the node tells whether the chain has
/// the bond of a proof, see <see cref="BondProofVerifier"/>, and what it holds.
/// </summary>
/// <remarks>
/// A node that cannot be reached, or that takes too long, is not the same as a bond that is not there: the relay is
/// told that nothing is known, and whoever showed the bond can come back. After such an answer the node is left
/// alone for a while, so that the events that come in meanwhile do not each wait for a node that is not there.
/// </remarks>
public sealed class NodeBondChecker : IDisposable
{
	/// <summary>How long the node is given to answer about a bond.</summary>
	public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

	/// <summary>For how long the node is not asked again after it could not be asked.</summary>
	public static readonly TimeSpan PauseAfterFailure = TimeSpan.FromSeconds(30);

	private readonly BondProofVerifier _verifier;
	private readonly Func<long> _getMilliseconds;
	private readonly TimeSpan _answerTimeout;

	/// <summary>The node is asked about one bond at a time.</summary>
	private readonly SemaphoreSlim _lock = new(1, 1);

	/// <summary>Until when the node is left alone, on the clock of <see cref="_getMilliseconds"/>.</summary>
	private long _pausedUntil = long.MinValue;

	/// <param name="getMilliseconds">A clock that only goes forward, in milliseconds. The clock of the computer if none is given.</param>
	/// <param name="answerTimeout">How long the node is given to answer, if not <see cref="AnswerTimeout"/>.</param>
	public NodeBondChecker(IRPCClient rpcClient, Func<long>? getMilliseconds = null, TimeSpan? answerTimeout = null)
	{
		_verifier = new BondProofVerifier(rpcClient);
		_getMilliseconds = getMilliseconds ?? (() => Environment.TickCount64);
		_answerTimeout = answerTimeout ?? AnswerTimeout;
	}

	/// <summary>Asks the node whether the chain has the bond of a proof that is signed for the given key.</summary>
	/// <param name="publicKey">The key that the proof is for, as events name it.</param>
	public async Task<BondCheck> CheckAsync(string publicKey, BondProof proof, CancellationToken cancellationToken)
	{
		if (_getMilliseconds() < Interlocked.Read(ref _pausedUntil))
		{
			return new BondCheck(BondCheckResult.Unavailable);
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(_answerTimeout);

		try
		{
			await _lock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

			var verification = VerifyAsync(publicKey, proof, timeoutCts.Token);

			// The next question waits until this one is over, also if nobody waits for its answer any more.
			_ = verification.ContinueWith(
				task =>
				{
					_ = task.Exception;
					ReleaseLock();
				},
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);

			var amount = await verification.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

			return amount > 0
				? new BondCheck(BondCheckResult.Found, amount)
				: new BondCheck(BondCheckResult.NotFound);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			// The node is not there, refuses to talk, or takes too long: that says nothing about the bond.
			Logger.LogDebug($"The node could not be asked about a fidelity bond: {ex.Message}");
			Interlocked.Exchange(ref _pausedUntil, _getMilliseconds() + (long)PauseAfterFailure.TotalMilliseconds);

			return new BondCheck(BondCheckResult.Unavailable);
		}
	}

	public void Dispose() => _lock.Dispose();

	private async Task<long> VerifyAsync(string publicKey, BondProof proof, CancellationToken cancellationToken)
	{
		// The node is asked from another thread than the one of the visitor's connection.
		await Task.Yield();

		var amounts = await _verifier.VerifyAsync([(publicKey, proof)], cancellationToken).ConfigureAwait(false);

		return amounts[0];
	}

	private void ReleaseLock()
	{
		try
		{
			_lock.Release();
		}
		catch (ObjectDisposedException)
		{
			// The application is closing, and nobody asks any more.
		}
	}
}
