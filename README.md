# Black Hole

An open-source, non-custodial, privacy-focused Bitcoin wallet for the Linux desktop that asks only your own Bitcoin node about the blockchain.

Black Hole is a fork of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi). It keeps the Tor integration and the WabiSabi coinjoins of Wasabi Wallet and drops most of the rest.

## What it does

- **One wallet.** Its backup is SLIP39: 20 recovery words by default, or several shares with a threshold if you choose to split it. The password encrypts the wallet file on the computer only; the backup alone recovers the wallet.
- **Taproot only.** Every address of the wallet is a Taproot (P2TR) address.
- **A new address for every payment.** *New Address* on the *Receive* tab generates an address that was not given out before.
- **Your node only.** Blocks, unconfirmed transactions and fee estimations come from the RPC interface of your Bitcoin node, and transactions are broadcast through it. The wallet scans the blocks of the node, starting with the block the wallet was created at.
- **Coinjoins** with the WabiSabi coordinator that you choose, over Tor.
- **Manual coin control** when sending.
- **Fidelity bonds**: coins that are locked until a block height that you name. See [Fidelity bonds](#fidelity-bonds).
- **Offers as a market maker**: up to two coins and your prices for anonymity points, posted to Nostr relays over Tor. See [Maker offers](#maker-offers).

## The window

The title bar lists the pages of the wallet as tabs, once the wallet is decrypted:

- **History**: the transactions of the wallet. The wallet opens with it.
- **Send**: the coins of the wallet, and above them the address, the amount and the fee rate of a payment. Tick the coins that the transaction may spend, fill in the rest, and *Continue* shows the transaction; nothing is sent before you confirm it there. The fee rate starts as what your node estimates for a confirmation within two blocks, and you can type another one.
- **Receive**: *New Address* generates an address and shows it. The list has the addresses that were generated and have not been paid to yet; a right click on one copies it.
- **WabiSabi**: the WabiSabi coinjoins of the wallet with their controls, and the coins of the wallet. No coin is ticked until you tick it: a coin takes part in coinjoins only once you chose it, and that goes for a coin that arrives and for what a coinjoin gives you as well. A round gets the ticked coins that it accepts, ten of them at most. Nothing coinjoins by itself: *Play* starts one round, and when that round is over, however it ended, the coinjoin is stopped until you press *Play* again.
- **JoinMarket**: what makes the wallet a market maker of JoinMarket-style coinjoins, which is all that there is of that protocol so far: its fidelity bonds (*Bond*) and its offer (*Offer*). The list of market makers shows the offer of this wallet while it has one. It shows nobody else's: the wallet does not look for makers yet.
- **ZeroLink**: a placeholder. The wallet does nothing of that protocol yet.

To the right of the tabs the title bar tells the fee rate, how many blocks the wallet has processed and the balance. In a window that is too narrow for all of it these give way, the fee rate first and the balance last, so that the tabs and the buttons stay. *Wallet Keys*, the extended public keys of the wallet, is in *Settings → Connections*.

## What was removed from Wasabi Wallet

Windows and macOS support, SegWit v0 addresses, hardware wallets, passphrases, BIP39 seed phrases, fiat exchange rates, the Branch and Bound search for changeless transactions, the dust attack limit, the Scheme scripting language, multiple wallets, confirmation time estimations, labels, the "ready to sign" signalling of coinjoins, the coinjoin strategies, the stop coinjoin threshold, the automatic start of coinjoins and their going on from round to round, the random and score-driven choice of the coins for a round with its isolation of non-private coins, the privacy progress with its ring, the anonymity score and its target, the sorting of the coins into private, semi-private and non-private ones, compact block filter synchronization together with the peer to peer network stack, and every third party service (fee and exchange rate providers, block explorers, update checks).

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

A wallet watches 210 unused addresses after the last one that was paid to (its gap limit). A recovered wallet does not find a payment whose address follows more unused addresses than that until the gap limit is raised, in *Advanced Recovery Options* or later with *Resync Wallet* in *Settings → Connections*. Addresses stay unused when they were generated on the *Receive* tab and never paid to. Coinjoin rounds that fail leave change addresses unused too, but they do not take the wallet beyond its gap limit, see [Coinjoin](#coinjoin).

## Data

Black Hole keeps its data in `~/.blackhole/client`. The folder button in the top right corner of the window shows that folder in the file manager; the wallet, the config file and the logs are in it. It does not read or change the data of a Wasabi Wallet installation (`~/.walletwasabi`), and its Tor listens on other ports (37250 and 37251), so both can run on the same computer.

Started with `--loglevel=debug`, Black Hole writes into `Logs.txt` what the wallet asks the node for while it synchronizes: every block when it is asked for, when it arrives and when it is processed. That tells what a wallet that seems to be stuck is waiting for. The file starts anew when it reaches 10 MB, which is about 25,000 blocks; with `--loglevel=trace` it has no limit.

## Coinjoin

Set the coordinator in *Settings → Coordinator*. The coordinator of this repository (`WalletWasabi.Coordinator`) speaks the same protocol as the wallet. The coordinators of Wasabi Wallet expect the "ready to sign" message that Black Hole does not send, so do not count on them.

Coinjoins are started by hand. *Play* on the *WabiSabi* tab starts one round with the coins that are ticked in the list below it, and when the round is over the coinjoin is stopped; the box tells how the round ended. The round gets the ticked coins that it accepts, wherever they come from and however often they have been in a coinjoin, but ten at most: if more are ticked, the ten largest go into the round and the others stay ticked for a later one. A ticked coin also stays out if it cannot be registered at that time, for example because it is unconfirmed or banned by the coordinator, or if the round does not take its amount. The ticks are kept in the wallet file. The coins that a coinjoin gives you are new coins and unticked: tick them to submit them into another round. With no coin ticked, *Play* tells that no coin is ticked and nothing else happens. Nor does the wallet call a coin private, semi-private or non-private: the preview of a transaction warns about what the transaction gives away, such as linking coins to each other, whichever coins it spends.

Every round gets addresses that no round had before. A round that fails has shown the addresses of your outputs to the coordinator and, once its transaction was up for signing, to everybody in it; an address that came back in a later round would tell that the same wallet was in both. So the addresses of a failed round are left behind: the blame round that follows a round that not everybody signed gets new ones, and so does the next coinjoin after a round whose transaction could not be broadcast. The wallet file knows that an address is taken before the coordinator gets to see it, so closing the wallet in the middle of a round does not bring it back either. A wallet that is recovered from its backup has no such record: nothing on the chain tells which addresses a failed round took, so it may give them out again. Only the address of a payment that you queued for a coinjoin stays the same from round to round, because it is not the wallet's to change.

There is one exception, for the sake of the gap limit. A round that fails leaves up to ten change addresses unused, and a wallet that is recovered from its backup watches 210 unused change addresses after the last one that was paid to. So once 200 change addresses in a row have gone to rounds that failed, the rounds after them share the last ten of the 210: these ten come back from round to round. That ends when something is paid to one of them, by a round that succeeds or by the change of a payment. The wallet counts anew from the address that was paid to, and every round gets new addresses again. While rounds have had all ten, the change of a payment goes to the last of them, since an address beyond the 210 is one that a recovered wallet would not find.

The lists show every coin with its *anonymity points*. An output of a transaction is *matched* if another output of the same transaction has the same amount. A transaction is worth as many points as it has matched outputs, but not more than it has inputs that were matched outputs of the transactions that created them: the lower of the two numbers counts. The matched outputs of the transaction carry these points for as long as they are unspent; its other outputs, such as change, carry none. Nothing is inherited or averaged: the points of a coin do not depend on the points of the coins that paid for it, and they are gone when the coin is spent. A first coinjoin of coins that were never matched is therefore worth 0 points, and so is an ordinary payment. To count the inputs the wallet asks your node for the transactions that created them, also for the inputs of others. A coin shows *?* until the node has answered; a pruned node may no longer have the blocks that it takes.

Matched outputs are also what the wallet takes as unlinked. A coin that one of your transactions makes counts as linked to the coins that the transaction spent, unless it is a matched output of that transaction; change, whose amount occurs once, stays linked. The preview of a payment warns when the payment would spend coins together that nothing links so far, and when the wallet picks the coins of a payment out of the ticked ones, it takes confirmed coins before unconfirmed ones, then as few unlinked coins as the amount needs, then the coins closest to the amount. The points of a coin do not make the wallet prefer it or hold it back.

A coinjoin splits your coins into standard amounts. What is left and is too small for an output of its own goes to the miners. *Dust forfeit limit* in *Settings → Coordinator* limits that: if a coinjoin would leave more than the limit, the leftover is added to one of your outputs instead. You keep the money, but that output has a non-standard amount and gains less privacy than the others. The limit is 0.00005 BTC unless you change it, and it cannot be set above 0.0001 BTC. Because of this limit the wallet also joins rounds whose smallest output is large ("whale rounds"), which Wasabi Wallet refuses from 0.0001 BTC on: what such a round cannot put into an output is kept the same way. If a round cannot keep the limit at all, the wallet gives up that round instead of signing it.

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

## Maker offers

A market maker of JoinMarket-style coinjoins lets a taker put its coins into the coinjoin of the taker. *Offer* on the *JoinMarket* tab makes the offer of this wallet: tick one or two coins and name three numbers.

- **Minimum fee rate**, in sat/vB: the lowest mining fee rate of a coinjoin that your coins go into.
- **Bid**, in sats per anonymity point: what you pay for a point.
- **Ask**, in sats per anonymity point: what you want for a point. It cannot be lower than the bid.

What the two prices are for: a coinjoin destroys the anonymity points of the coins that go into it, and the coins that come out of it have the points of the new transaction. So the coinjoin of a taker swaps the points of your coins for others. If you have more points afterwards, you pay the taker the bid for each of them; if you have fewer, the taker pays you the ask for each.

Black Hole does nothing with an offer yet except post it: it does not look for the offers of others, and it does not answer a taker. No coinjoin of this kind exists so far.

- **Where it goes.** *Post* sends the offer to Nostr relays: `relay.primal.net`, `relay.damus.io` and `nos.lol`, or the ones that you name at the start with `--nostrrelays=wss://one.example;wss://two.example`. They are reached through Tor, unless Tor is turned off. Nothing is sent to a relay before you post an offer. This and the coordinator of your WabiSabi coinjoins are the only servers that Black Hole talks to besides your node.
- **What it tells.** The offer is an event of the kind for the discovery of coinjoins (15750, a [draft](https://github.com/nostr-protocol/nips/pull/384)) with the tags `type` = `BlackHoleJoinMarket` and `network` = `mainnet`, `signet` or `regtest`. Its content is JSON, for example `{"minFeeRate":2.5,"bid":100,"ask":150,"liquidity":50000000}`: the fee rate, the two prices, and what the ticked coins hold together, in sats. The third tag of that kind, `endpoint`, is left out: there is no place yet where a taker reaches a maker.
- **What it does not tell.** Which coins they are, and what each of them holds. Whoever reads the relays can still search the chain for coins of that amount, most easily when the offer has one coin. Every offer is signed with a new key, so two offers cannot be told to come from the same wallet by their keys.
- **How long it stands.** Until you take it back with *Withdraw offer*, until its coins are spent, or until you close Black Hole. Readers of that kind of event take one that is older than 15 minutes for gone, so a standing offer is posted again every 10 minutes. The kind is replaceable, which means that a relay keeps only the newest event of a key: an offer is changed by posting it again, and taken back by an event that tells nothing. An offer is not stored. After a restart there is none until you post one.
- **Coins.** A coin of the offer that is spent leaves the offer, which is posted again with what is left. The offer keeps no coin from being spent or coinjoined.
- **When no relay can be reached.** An offer that no relay took is not made, and the page tells why. If a standing offer cannot be posted again, its row in the list shows a warning, and it is tried again every minute.

## License

[MIT](LICENSE.md), as Wasabi Wallet.
