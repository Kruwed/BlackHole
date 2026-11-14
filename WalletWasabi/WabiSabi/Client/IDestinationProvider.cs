namespace WalletWasabi.WabiSabi.Client;

public interface IDestinationProvider
{
	ImmutableHashSet<ScriptType> SupportedScriptTypes { get; }

	IEnumerable<IDestination> GetNextDestinations(int count);

	public void TrySetScriptStates(KeyState state, IEnumerable<Script> scripts);
}
