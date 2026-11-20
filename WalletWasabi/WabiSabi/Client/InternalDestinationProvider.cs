namespace WalletWasabi.WabiSabi.Client;

public class InternalDestinationProvider : IDestinationProvider
{
	public InternalDestinationProvider(KeyManager keyManager)
	{
		_keyManager = keyManager;
	}

	private readonly KeyManager _keyManager;

	/// <remarks>The wallet file knows that the keys of the addresses were given out when this returns, see <see cref="KeyManager.GetNextCoinJoinKeys"/>.</remarks>
	public IEnumerable<IDestination> GetNextDestinations(int count) =>
		_keyManager.GetNextCoinJoinKeys(count).Select(key => key.GetAddress(_keyManager.GetNetwork())).ToArray();

	public void MarkAsPaid(IEnumerable<Script> scripts) =>
		_keyManager.MarkAsPaid(scripts);

	/// <summary>The wallet only has Taproot addresses.</summary>
	public ImmutableHashSet<ScriptType> SupportedScriptTypes { get; } = [ScriptType.Taproot];
}
