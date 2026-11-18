# Black Hole

An open-source, non-custodial, privacy-focused Bitcoin wallet for the Linux desktop that talks only to your own Bitcoin node.

Black Hole is a fork of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi). It keeps the Tor integration and the WabiSabi coinjoins of Wasabi Wallet and drops most of the rest.

## What it does

- **One wallet.** Its backup is SLIP39: 20 recovery words by default, or several shares with a threshold if you choose to split it. The password encrypts the wallet file on the computer only; the backup alone recovers the wallet.
- **Taproot only.** Every address of the wallet is a Taproot (P2TR) address.
- **A new address for every payment.** The receive screen shows an address that was not given out before, a new one every time it is opened.
- **Your node only.** Blocks, unconfirmed transactions and fee estimations come from the RPC interface of your Bitcoin node, and transactions are broadcast through it. The wallet scans the blocks of the node, starting with the block the wallet was created at.
- **Coinjoins** with the WabiSabi coordinator that you choose, over Tor.
- **Manual coin control** when sending.
- **Fidelity bonds**: coins that are locked until a date, in the format of JoinMarket. See [Fidelity bonds](#fidelity-bonds).

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

Before there is a wallet, *Connect to a full node* on the first screen asks for the two. It checks them with the node and, if the node answers and is on the right network, works with it right away. They are in *Settings → Connections* too, where a change takes effect after a restart. The gear in the top right corner of the window opens the settings, also before there is a wallet.

The same settings are `BitcoinRpcEndPoint` and `BitcoinRpcCredentialString` in `Config.json`.

Recovering a wallet scans every block since the activation of Taproot unless you tell the height the wallet was created at (*Advanced Recovery Options*). On mainnet the full scan takes hours.

A wallet watches 210 unused addresses after the last one that was paid to (its gap limit). A recovered wallet does not find a payment whose address follows more unused addresses than that until the gap limit is raised, in *Advanced Recovery Options* or later with *Resync Wallet* in *Settings → Connections*.

## Data

Black Hole keeps its data in `~/.blackhole/client`. The folder button in the top right corner of the window shows that folder in the file manager; the wallet, the config file and the logs are in it. It does not read or change the data of a Wasabi Wallet installation (`~/.walletwasabi`), and its Tor listens on other ports (37250 and 37251), so both can run on the same computer.

Started with `--loglevel=debug`, Black Hole writes into `Logs.txt` what the wallet asks the node for while it synchronizes: every block when it is asked for, when it arrives and when it is processed. That tells what a wallet that seems to be stuck is waiting for. The file starts anew when it reaches 10 MB, which is about 25,000 blocks; with `--loglevel=trace` it has no limit.

## Coinjoin

Set the coordinator in *Settings → Coordinator*. The coordinator of this repository (`WalletWasabi.Coordinator`) speaks the same protocol as the wallet. The coordinators of Wasabi Wallet expect the "ready to sign" message that Black Hole does not send, so do not count on them.

A coinjoin splits your coins into standard amounts. What is left and is too small for an output of its own goes to the miners. *Dust forfeit limit* in the *Coinjoin Settings* of the wallet limits that: if a coinjoin would leave more than the limit, the leftover is added to one of your outputs instead. You keep the money, but that output has a non-standard amount and gains less privacy than the others. The limit is 0.00005 BTC unless you change it, and it cannot be set above 0.0001 BTC. Because of this limit the wallet also joins rounds whose smallest output is large ("whale rounds"), which Wasabi Wallet refuses from 0.0001 BTC on: what such a round cannot put into an output is kept the same way. If a round cannot keep the limit at all, the wallet gives up that round instead of signing it.

## Fidelity bonds

A fidelity bond is coins that are locked until a date. A maker of JoinMarket coinjoins shows one to prove that it has something to lose, which makes it costly to pose as many makers. *Bond* on the wallet screen creates, lists and redeems such bonds in the format of JoinMarket. Black Hole does not use them for anything yet: it has no JoinMarket-style coinjoin so far, and it makes no proof of a bond and shows none to anyone.

- **Format.** A bond is an output that pays to the hash (P2WSH) of the script `<locktime> OP_CHECKLOCKTIMEVERIFY OP_DROP <public key> OP_CHECKSIG`. It is the one thing in the wallet that is not Taproot.
- **Lock dates.** A bond unlocks at the start of a month, at 00:00 UTC. The wallet offers the months of the next ten years; the format has every month until December 2099. Locked coins cannot be spent before that date by anyone, and there is no way to undo it.
- **Keys.** The key of a bond is `m/86'/0'/0'/2/i` on mainnet, where `i` counts the months since January 2020. It is a key of the account of the wallet, so the recovery words bring the bonds back: a recovered wallet finds them when it scans the blocks. JoinMarket takes the keys of its bonds from `m/84'/0'/0'/2/i`, so a bond of one of the two wallets is not found by the other.
- **Creating.** Choose the month, pick the coins and the amount. The transaction is previewed and sent like a payment. Every month has one address, and it can be paid from another wallet too; such a bond is listed once its transaction is confirmed. Every payment to the address is a bond of its own, and several small bonds count for less than one large one, so pay to it once.
- **Not part of the balance.** Locked coins are not in the balance and not among the coins of the wallet, and no payment and no coinjoin spends them.
- **Bond value.** The list tells what a bond counts for by the formula of JoinMarket: `(amount × (e^(0.015 × years) − 1))^1.3`, with the amount in satoshis and the years from the confirmation of the bond to its lock date. The number has no unit; it compares bonds. After the lock date it wears off.
- **Redeeming.** Once the chain is past the lock date, *Redeem* pays a bond back to the wallet, to a new address of it, less the mining fee. The chain goes by the median time of its last eleven blocks, which is about an hour behind the clock.
- **Privacy.** A bond that is shown to others is public, and so is the history of the coins in it.

## License

[MIT](LICENSE.md), as Wasabi Wallet.
