namespace WalletWasabi.WabiSabi.Client;

public interface IDestinationProvider
{
	ImmutableHashSet<ScriptType> SupportedScriptTypes { get; }

	/// <summary>
	/// Gives out the addresses for the outputs of one coinjoin round. An address is given out once, so a round never gets
	/// an address that another round had, whether that round failed or not.
	/// </summary>
	IEnumerable<IDestination> GetNextDestinations(int count);
}
