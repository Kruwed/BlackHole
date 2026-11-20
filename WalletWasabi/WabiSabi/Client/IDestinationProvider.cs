namespace WalletWasabi.WabiSabi.Client;

public interface IDestinationProvider
{
	ImmutableHashSet<ScriptType> SupportedScriptTypes { get; }

	/// <summary>
	/// Gives out the addresses for the outputs of one coinjoin round. An address is given out once, so a round does not get
	/// an address that another round had, whether that round failed or not. Only where rounds that failed would take the
	/// wallet beyond its gap limit, the last addresses within it are given out again until one of them is paid to.
	/// </summary>
	IEnumerable<IDestination> GetNextDestinations(int count);

	/// <summary>
	/// Tells that a round paid to these addresses, or may have: its transaction was broadcast, or how it ended is not known.
	/// No round gets them again.
	/// </summary>
	void MarkAsPaid(IEnumerable<Script> scripts);
}
