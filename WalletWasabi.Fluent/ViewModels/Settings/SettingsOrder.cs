using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>What a box of the Wallet column of the settings is for.</summary>
public enum WalletTile
{
	Recover,
	Create,
	Configure,

	/// <summary>Leads to where coins of the wallet are locked in a fidelity bond.</summary>
	CreateBond
}

/// <summary>How the settings were left.</summary>
public enum SettingsExit
{
	/// <summary>They were put away.</summary>
	Closed,

	/// <summary>The user asked for a fidelity bond. The settings made way for the place where coins are locked.</summary>
	CreateBond
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
	/// Tells what the two boxes of the Wallet column are. Without a wallet the upper one creates a wallet and the
	/// lower one recovers one. There is one wallet at most, so once it is there the boxes have no wallet left to make:
	/// the upper one configures the wallet, and the lower one creates a fidelity bond, which takes a wallet. That is so
	/// for a wallet that was created and for one that was recovered.
	/// </summary>
	public static (WalletTile Upper, WalletTile Lower) GetWalletTiles(bool hasWallet) =>
		hasWallet
			? (WalletTile.Configure, WalletTile.CreateBond)
			: (WalletTile.Create, WalletTile.Recover);

	/// <summary>
	/// Whether a fidelity bond can be created: there is a wallet, created or recovered, and it is decrypted. A bond
	/// locks coins of the wallet, and a wallet shows its coins once its password was given.
	/// </summary>
	public static bool CanCreateBond(bool hasWallet, bool isWalletDecrypted) => hasWallet && isWalletDecrypted;

	/// <summary>
	/// What the box that creates a bond says while it cannot be pressed: the wallet is there, and is not decrypted
	/// yet. Nothing while it can be pressed.
	/// </summary>
	public static string? DescribeBondTile(bool isWalletDecrypted) =>
		isWalletDecrypted ? null : "Decrypt the wallet to create a bond.";

	/// <summary>
	/// Whether the box that creates a bond has its mark: a fidelity bond of the wallet is locked at the moment. A bond
	/// is locked from the confirmation of the transaction that pays to it until the chain reaches its unlock height:
	/// one that waits for its confirmation is not locked yet, and one that has unlocked is not locked any more. A
	/// wallet that is not decrypted tells nothing about its coins, so its box has no mark.
	/// </summary>
	/// <param name="bonds">Where each bond of the wallet stands.</param>
	public static bool HasLockedBond(bool isWalletDecrypted, IEnumerable<FidelityBondState> bonds) =>
		isWalletDecrypted && bonds.Any(state => state == FidelityBondState.Locked);
}
