namespace WalletWasabi.WabiSabi.Client.CoinJoin.Manager;

/// <summary>
/// The kinds of coinjoin that a wallet takes part in with a coordinator. A wallet has one coinjoin at a time, of one of
/// them.
/// </summary>
public enum CoinJoinProtocol
{
	/// <summary>A round that takes up to ten coins of any amounts and gives outputs of standard denominations.</summary>
	WabiSabi,

	/// <summary>
	/// A round of one standard denomination: the wallet registers one coin of that amount and gets one output, of the
	/// denomination less the fee. The credentials and the phases are the ones of WabiSabi.
	/// </summary>
	ZeroLink
}
