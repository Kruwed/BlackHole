# Black Hole

An open-source, non-custodial, privacy-focused Bitcoin wallet for the Linux desktop that talks only to your own Bitcoin node.

Black Hole is a fork of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi). It keeps the Tor integration and the WabiSabi coinjoins of Wasabi Wallet and drops most of the rest.

## What it does

- **One wallet.** Its backup is SLIP39: 20 recovery words by default, or several shares with a threshold if you choose to split it. The password encrypts the wallet file on the computer only; the backup alone recovers the wallet.
- **Taproot only.** Every address of the wallet is a Taproot (P2TR) address.
- **A new address for every payment.** *New Address* on the *Receive* tab generates an address that was not given out before.
- **Your node only.** Blocks, unconfirmed transactions and fee estimations come from the RPC interface of your Bitcoin node, and transactions are broadcast through it. The wallet scans the blocks of the node, starting with the block the wallet was created at.
- **Coinjoins** with the WabiSabi coordinator that you choose, over Tor.
- **Manual coin control** when sending.
- **Fidelity bonds**: coins that are locked until a block height that you name. See [Fidelity bonds](#fidelity-bonds).

## The window

The title bar lists the pages of the wallet as tabs, once the wallet is decrypted:

- **History**: the transactions of the wallet. The wallet opens with it.
- **Send**: the coins of the wallet, and above them the address, the amount and the fee rate of a payment. Tick the coins that the transaction may spend, fill in the rest, and *Continue* shows the transaction; nothing is sent before you confirm it there. The fee rate starts as what your node estimates for a confirmation within two blocks, and you can type another one.
- **Receive**: *New Address* generates an address and shows it. The list has the addresses that were generated and have not been paid to yet; a right click on one copies it.
- **WabiSabi**: the privacy progress of the wallet, its WabiSabi coinjoins with their controls and settings, and the coins of the wallet. The ticked coins may be submitted into rounds; untick a coin to keep it out of coinjoins. A coin is ticked until you untick it, so a coin that arrives may be submitted.
- **JoinMarket**: the fidelity bonds, which are all that there is of that protocol so far, and a list of market makers that is a placeholder: it has its columns and no rows yet.
- **ZeroLink**: a placeholder. The wallet does nothing of that protocol yet.

To the right of the tabs the title bar tells the fee rate, how many blocks the wallet has processed and the balance. In a window that is too narrow for all of it these give way, the fee rate first and the balance last, so that the tabs and the buttons stay. *Wallet Keys*, the extended public keys of the wallet, is in *Settings → Connections*.

## What was removed from Wasabi Wallet

Windows and macOS support, SegWit v0 addresses, hardware wallets, passphrases, BIP39 seed phrases, fiat exchange rates, the Branch and Bound search for changeless transactions, the dust attack limit, the Scheme scripting language, multiple wallets, confirmation time estimations, labels, the "ready to sign" signalling of coinjoins, the coinjoin strategies, the stop coinjoin threshold, compact block filter synchronization together with the peer to peer network stack, and every third party service (fee and exchange rate providers, block explorers, update checks).

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

A coinjoin splits your coins into standard amounts. What is left and is too small for an output of its own goes to the miners. *Dust forfeit limit* in the *Coinjoin Settings* on the *WabiSabi* tab limits that: if a coinjoin would leave more than the limit, the leftover is added to one of your outputs instead. You keep the money, but that output has a non-standard amount and gains less privacy than the others. The limit is 0.00005 BTC unless you change it, and it cannot be set above 0.0001 BTC. Because of this limit the wallet also joins rounds whose smallest output is large ("whale rounds"), which Wasabi Wallet refuses from 0.0001 BTC on: what such a round cannot put into an output is kept the same way. If a round cannot keep the limit at all, the wallet gives up that round instead of signing it.

## Fidelity bonds

A fidelity bond is coins that are locked until the chain reaches a block height. A maker of JoinMarket-style coinjoins shows one to prove that it has something to lose, which makes it costly to pose as many makers. *Bond* on the *JoinMarket* tab creates, lists and redeems such bonds. Black Hole does not use them for anything yet: it has no coinjoin of that kind so far, and it makes no proof of a bond and shows none to anyone. The bonds are not the ones of JoinMarket, which does not understand them: they are Taproot outputs, and they unlock at a block height instead of a date.

- **Format.** A bond is a Taproot output that can be spent by one script only: `<unlock height> OP_CHECKLOCKTIMEVERIFY OP_DROP <public key> OP_CHECKSIG`. Its internal key is the point that BIP341 names for outputs without a key path (`50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0`). Nobody has the private key of that point, so whoever is shown the script can check that there is no way around the timelock. On the chain a bond looks like any other Taproot output until it is redeemed.
- **Unlock height.** You name the exact block height. The wallet tells the day that the chain is expected to get there, at a block every ten minutes; it is the blocks that decide, not the calendar. A bond can be redeemed once the chain has reached the height: the transaction that redeems it is valid from the next block on. The height has to be ahead of the chain, and no more than 525,600 blocks ahead, which is about ten years. Locked coins cannot be spent before that by anyone, and there is no way to undo it.
- **Keys.** The key of a bond is `m/86'/0'/0'/2/h` on mainnet, where `h` is the unlock height. It is a key of the account of the wallet, and every height has its own.
- **Creating.** Name the height, pick the coins and the amount. The transaction is previewed and sent like a payment. Every height has one address, and it can be paid from another wallet too; such a bond is listed once its transaction is confirmed. Every payment to the address is a bond of its own, and several small bonds count for less than one large one, so pay to it once.
- **Balance and history.** The coins in a bond belong to the wallet and are part of its balance, though it cannot spend them yet. The balance at the top of the window is everything that the wallet owns, in one amount; *Bond* lists what is locked. In the history, locking and unlocking have icons of their own, and their amount is what they changed in the balance: the mining fee, when the coins came from the wallet or go back to it. The details of the transaction tell the amount that was locked or unlocked.
- **Bond value.** The list tells what a bond counts for by the formula of JoinMarket, with the time counted in blocks: `(amount × (e^(0.015 × years) − 1))^1.3`, with the amount in satoshis and the years from the block that confirmed the bond to its unlock height, at 52,594.92 blocks a year. The number has no unit; it compares bonds. After the unlock height it wears off.
- **Redeeming.** Once the chain has reached the unlock height, *Redeem* pays a bond back to the wallet, to a new address of it, less the mining fee.
- **Recovery.** There are too many heights to watch the addresses of all of them, so the wallet watches the ones that it gave out, and the wallet file remembers them. The recovery words do not tell them. A wallet that is recovered, or told to *Resync Wallet*, therefore looks for a bond at every height that it could have chosen, from the first block that it goes through to ten years above the chain. Getting ready for that takes about a minute before the first block is processed. This finds the bonds that are in the blocks. A bond address from before the recovery that is paid after it, or a bond that another copy of the wallet made, is found by resyncing again.
- **Privacy.** A bond that is shown to others is public, and so is the history of the coins in it.

## License

[MIT](LICENSE.md), as Wasabi Wallet.
