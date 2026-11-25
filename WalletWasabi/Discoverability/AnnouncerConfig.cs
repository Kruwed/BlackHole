using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client.Protocols;
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace WalletWasabi.Discoverability;

public record AnnouncerConfig
{
	public string CoordinatorName { get; init; } = "Coordinator";
	public bool IsEnabled { get; init; } = false;
	public string CoordinatorDescription { get; init; } = "WabiSabi Coinjoin Coordinator";
	public string CoordinatorUri { get; set; } = "https://api.example.com/";
	public uint AbsoluteMinInputCount { get; init; } = 21;
	public string ReadMoreUri { get; set; } = "https://api.example.com/";
	public string[] RelayUris { get; init;  } = ["wss://relay.primal.net"];
	public string Key { get; init; } = InitKey();

	/// <summary>
	/// The proof of a fidelity bond that stands behind this coordinator, or nothing. A wallet that has a bond makes the
	/// proof for the key of the coordinator, and writes it as one text, see <see cref="FidelityBonds.BondProof.ToText"/>.
	/// The announcement shows it, so that the relays which ask for a bond take the announcement.
	/// </summary>
	public string BondProof { get; init; } = "";

	private static string InitKey()
	{
		using var key = new Key();
		using var privKey = ECPrivKey.Create(key.ToBytes());
		return privKey.ToNIP19();
	}
}
