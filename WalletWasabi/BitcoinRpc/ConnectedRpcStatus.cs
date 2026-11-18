using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Helpers;

namespace WalletWasabi.BitcoinRpc;

/// <param name="MedianTimePast">
/// The median of the times of the last eleven blocks. A timelock that names a time is over once this time is past it,
/// which is about an hour later than by the clock.
/// </param>
public record ConnectedRpcStatus(
	ulong Headers,
	ulong Blocks,
	Result<int, RPCResponse> PeersCount,
	uint256 BestBlockHash,
	bool Pruned,
	bool InitialBlockDownload,
	DateTimeOffset MedianTimePast)
{
	public override string? ToString() =>
		PeersCount.Match(
			peersCount => (peersCount, Synchronized) switch
			{
				(0, _) => "has no peers",
				(_, true) => "is synchronized",
				(_, false) => "is synchronizing..."
			},
			response => (int) response.Error.Code switch
			{
				-32075 => "is connected with restrictions",
				_ => response.Error.Message
			});

	public bool Synchronized =>  Blocks == Headers;
}
