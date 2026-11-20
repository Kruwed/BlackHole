namespace WalletWasabi.WabiSabi.Client;

public class InternalDestinationProvider : IDestinationProvider
{
	public InternalDestinationProvider(KeyManager keyManager)
	{
		_keyManager = keyManager;
	}

	private readonly KeyManager _keyManager;

	/// <remarks>The keys of the addresses are used from here on, and the wallet file knows it, see <see cref="KeyManager.GetNextCoinJoinKeys"/>.</remarks>
	public IEnumerable<IDestination> GetNextDestinations(int count) =>
		_keyManager.GetNextCoinJoinKeys(count).Select(key => key.GetAddress(_keyManager.GetNetwork())).ToArray();

	/// <summary>The wallet only has Taproot addresses.</summary>
	public ImmutableHashSet<ScriptType> SupportedScriptTypes { get; } = [ScriptType.Taproot];
}
