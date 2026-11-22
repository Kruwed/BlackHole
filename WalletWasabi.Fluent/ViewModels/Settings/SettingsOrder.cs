using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Fluent.Models.UI;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>What a box of the Wallet column of the settings is for.</summary>
public enum WalletTile
{
	Recover,
	Create,
	Configure
}

/// <summary>
/// The rules of the settings, which are the welcome as long as there is no wallet: in which order Tor, the node,
/// Nostr, the coordinator and the wallet are set up, and what the Wallet column offers.
/// </summary>
public static class SettingsOrder
{
	/// <summary>The connections from left to right, which is the order in which they are set up.</summary>
	public static readonly IReadOnlyList<Connection> Connections =
		[Connection.Tor, Connection.Node, Connection.Nostr, Connection.Coordinator];

	/// <summary>
	/// The connection whose turn it is on the welcome: the first one that the user is not through with. None once the
	/// user is through with all four: then it is the turn of the wallet.
	/// </summary>
	/// <param name="isDone">Whether the user is through with a connection: it is configured, or there is nothing to configure.</param>
	public static Connection? GetCurrent(Func<Connection, bool> isDone)
	{
		foreach (var connection in Connections)
		{
			if (!isDone(connection))
			{
				return connection;
			}
		}

		return null;
	}

	/// <summary>
	/// Whether the page of a connection can be opened. With a wallet every page can. Without one, the user sets them
	/// up one after the other: a page can be opened once the connections before it are done, and a connection that
	/// is done can be opened again.
	/// </summary>
	public static bool IsOpen(Connection connection, bool hasWallet, Func<Connection, bool> isDone) =>
		hasWallet || isDone(connection) || Connections.TakeWhile(x => x != connection).All(isDone);

	/// <summary>
	/// Whether a wallet can be recovered or created: there is none yet, and the user is through with the four
	/// connections. There can only be one wallet.
	/// </summary>
	public static bool CanAddWallet(bool hasWallet, Func<Connection, bool> isDone) =>
		!hasWallet && Connections.All(isDone);

	/// <summary>
	/// Tells what the two boxes of the Wallet column are. Without a wallet the upper one recovers a wallet and the
	/// lower one creates one. Once there is a wallet, the box that made it configures it: the lower one if the wallet
	/// was created, the upper one if it was recovered. The other box stays what it was, and can no longer be used.
	/// </summary>
	public static (WalletTile Upper, WalletTile Lower) GetWalletTiles(bool hasWallet, bool isRecovered) =>
		(hasWallet, isRecovered) switch
		{
			(false, _) => (WalletTile.Recover, WalletTile.Create),
			(true, false) => (WalletTile.Recover, WalletTile.Configure),
			(true, true) => (WalletTile.Configure, WalletTile.Create)
		};
}
