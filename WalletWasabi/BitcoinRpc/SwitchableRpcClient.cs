using NBitcoin.RPC;

namespace WalletWasabi.BitcoinRpc;

/// <summary>
/// The client of a node that can be exchanged for another node while the application runs.
/// Whoever was given this client talks to the new node from the next request on.
/// </summary>
public class SwitchableRpcClient(RPCClient rpcClient) : RpcClientBase(rpcClient)
{
	private volatile RPCClient _rpcClient = rpcClient;

	protected internal override RPCClient RpcClient => _rpcClient;

	/// <summary>
	/// Sends the requests to another node from now on. A request that is on its way is still answered by the previous node.
	/// </summary>
	public void SwitchTo(RPCClient rpcClient) => _rpcClient = rpcClient;
}
