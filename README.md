# Black Hole

An open-source, non-custodial, privacy-focused Bitcoin wallet for the Linux desktop that talks only to your own Bitcoin node.

Black Hole is a fork of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi). It keeps the Tor integration and the WabiSabi coinjoins of Wasabi Wallet and drops most of the rest.

## What it does

- **One wallet.** It is created and recovered with SLIP39 shares. The password encrypts the wallet file on the computer only; the shares alone recover the wallet.
- **Taproot only.** Every address of the wallet is a Taproot (P2TR) address.
- **Your node only.** Blocks, unconfirmed transactions and fee estimations come from the RPC interface of your Bitcoin node, and transactions are broadcast through it. The wallet scans the blocks of the node, starting with the block the wallet was created at.
- **Coinjoins** with the WabiSabi coordinator that you choose, over Tor.
- **Manual coin control** when sending.

## What was removed from Wasabi Wallet

Windows and macOS support, SegWit v0 addresses, hardware wallets, passphrases, BIP39 seed phrases, fiat exchange rates, the Branch and Bound search for changeless transactions, the dust attack limit, the Scheme scripting language, multiple wallets, confirmation time estimations, labels, the "ready to sign" signalling of coinjoins, compact block filter synchronization together with the peer to peer network stack, and every third party service (fee and exchange rate providers, block explorers, update checks).

## Requirements

- Linux on x64 or arm64.
- A Bitcoin node with the RPC interface enabled, for example Bitcoin Core with `server=1`. The node must have the blocks since the creation of the wallet, so a pruned node only works while it still has them.
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) to build from source.

## Build and run

```sh
git clone https://github.com/Kruwed/BlackHole.git
cd BlackHole/WalletWasabi.Fluent.Desktop
dotnet run
```

`WalletWasabi.Daemon` is the same wallet without the user interface; see its [README](WalletWasabi.Daemon/README.md). The names of the projects and of the namespaces are the ones of Wasabi Wallet.

## Connect it to your node

A Bitcoin Core on the same computer that uses its default data folder and its default RPC port is found without any configuration. For any other node, tell Black Hole

- **the RPC endpoint**: the address of the RPC interface of the node. The defaults are the ones of Bitcoin Core, `http://localhost:8332` on mainnet.
- **the RPC credentials**: `rpcuser:rpcpassword`, or `cookiefile=` followed by the path of the cookie file of the node. Left empty, the cookie file in the default data folder of Bitcoin Core (`~/.bitcoin`) is used.

Before there is a wallet, *Connect to a full node* on the first screen asks for the two. It checks them with the node and, if the node answers and is on the right network, works with it right away. Later they are in *Settings → Bitcoin*, where a change takes effect after a restart.

The same settings are `BitcoinRpcEndPoint` and `BitcoinRpcCredentialString` in `Config.json`.

Recovering a wallet scans every block since the activation of Taproot unless you tell the height the wallet was created at (*Advanced Recovery Options*). On mainnet the full scan takes hours.

## Data

Black Hole keeps its data in `~/.blackhole/client`. It does not read or change the data of a Wasabi Wallet installation (`~/.walletwasabi`), and its Tor listens on other ports (37250 and 37251), so both can run on the same computer.

## Coinjoin

Set the coordinator in *Settings → Coordinator*. The coordinator of this repository (`WalletWasabi.Coordinator`) speaks the same protocol as the wallet. The coordinators of Wasabi Wallet expect the "ready to sign" message that Black Hole does not send, so do not count on them.

## License

[MIT](LICENSE.md), as Wasabi Wallet.
