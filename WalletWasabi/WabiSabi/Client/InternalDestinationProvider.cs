namespace WalletWasabi.WabiSabi.Client;

public class InternalDestinationProvider : IDestinationProvider
{
	public InternalDestinationProvider(KeyManager keyManager)
	{
		_keyManager = keyManager;
	}

	private readonly KeyManager _keyManager;

	public IEnumerable<IDestination> GetNextDestinations(int count)
	{
		// Get all locked internal keys we have and assert we have enough.
		_keyManager.AssertLockedInternalKeysIndexedAndPersist(count);

		return _keyManager.GetNextCoinJoinKeys().Select(x => x.GetAddress(_keyManager.GetNetwork()));
	}

	public void TrySetScriptStates(KeyState state, IEnumerable<Script> scripts)
	{
		foreach (var hdPubKey in _keyManager.GetKeys(key => scripts.Any(key.ContainsScript)))
		{
			_keyManager.SetKeyState(state, hdPubKey);
		}

		_keyManager.ToFile();
	}

	/// <summary>The wallet only has Taproot addresses.</summary>
	public ImmutableHashSet<ScriptType> SupportedScriptTypes { get; } = [ScriptType.Taproot];
}
