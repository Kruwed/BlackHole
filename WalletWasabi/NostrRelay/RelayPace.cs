namespace WalletWasabi.NostrRelay;

/// <summary>
/// A pace for work that others can make the relay do: so many times in a row, and then so many times per second in
/// the long run.
/// </summary>
/// <param name="burst">How many times in a row.</param>
/// <param name="perSecond">How many times per second in the long run.</param>
/// <param name="getMilliseconds">A clock that only goes forward, in milliseconds. The clock of the computer if none is given.</param>
public sealed class RelayPace(int burst, double perSecond, Func<long>? getMilliseconds = null)
{
	private readonly Func<long> _getMilliseconds = getMilliseconds ?? (() => Environment.TickCount64);
	private readonly Lock _lock = new();

	private double _available = burst;
	private long? _lastRefill;

	/// <summary>Takes one turn, if the pace has one left.</summary>
	public bool TryTake()
	{
		lock (_lock)
		{
			var now = _getMilliseconds();

			_available = Math.Min(burst, _available + (now - (_lastRefill ?? now)) / 1000.0 * perSecond);
			_lastRefill = now;

			if (_available < 1)
			{
				return false;
			}

			_available -= 1;
			return true;
		}
	}
}
