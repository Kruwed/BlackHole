# Black Hole

An open-source, non-custodial, privacy-focused Bitcoin wallet for the Linux desktop that asks only your own Bitcoin node about the blockchain.

Black Hole is a fork of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi). It keeps the Tor integration and the WabiSabi coinjoins of Wasabi Wallet and drops most of the rest.

## What it does

- **One wallet.** Its backup is SLIP39: 20 recovery words by default, or several shares with a threshold if you choose to split it. Before the wallet is created you confirm the backup that you wrote down, by putting every word of it into its place, of every share; no word is filled in for you, and the confirmation cannot be skipped. The password encrypts the wallet file on the computer only; the backup alone recovers the wallet.
- **Taproot only.** Every address of the wallet is a Taproot (P2TR) address.
- **A new address for every payment.** *Receive* lists the addresses that await a payment, and *New Address* there generates one that was not given out before. It is in the dialog that *Coinjoin* on the *Coins* tab opens.
- **Your node only.** Blocks, unconfirmed transactions and fee estimations come from the RPC interface of your Bitcoin node, and transactions leave through the private broadcast of the node, see [Private broadcast](#private-broadcast). The wallet scans the blocks of the node, starting with the block the wallet was created at.
- **Coinjoins** with the coordinator that you choose, over Tor: WabiSabi rounds, and ZeroLink rounds, which take one coin of a standard amount from every participant and give one output each. See [Coinjoin](#coinjoin) and [ZeroLink rounds](#zerolink-rounds).
- **Manual coin control**: you tick the coins that a payment spends, that a coinjoin round gets, that a bond locks and that an offer offers.
- **Fidelity bonds**: coins that are locked until a block height that you name. See [Fidelity bonds](#fidelity-bonds).
- **Offers as a market maker**: up to two coins and your prices for anonymity points, posted to Nostr relays over Tor. See [Maker offers](#maker-offers).

## The settings

The gear in the top right corner of the window opens the settings. They have five columns, and every column has two boxes: the upper one connects to something that exists, or recovers the wallet, and the lower one is for running, or creating, your own.

| *Tor* | *Node* | *Nostr* | *Coordinator* | *Wallet* |
| --- | --- | --- | --- | --- |
| Connect to Tor | Connect to Node | Connect to Nostr | Connect to Coordinator | Recover a Wallet |
| Run a Tor Relay | Run a Bitcoin Node | Run a Nostr Relay | Run a Coinjoin Coordinator | Create a Wallet |

The four *Run a …* boxes hold their place and do nothing yet. Each *Connect to …* box opens a page with two buttons:

- **Verify Connection** asks what the page names whether it answers, and tells the answer. It changes nothing.
- **Connect** does the same and, if it answers, makes Black Hole work with it from then on, without a restart, and saves it as the setting. Something that does not answer is not taken: the page tells why, and Black Hole keeps what it had.

What the pages are about:

- **Connect to Tor** asks for nothing. Black Hole starts its Tor when it starts; *Verify Connection* tells whether that Tor has connected to the Tor network, and *Connect* waits until it has. The page also has how Tor is used (*Enabled*; *Connect Only*, which uses a Tor that runs on the computer already; *Disabled*) and whether Tor is stopped when Black Hole closes. These two take a restart. With Tor turned off, and on regtest, where Tor is not used, there is nothing to connect to, and the two buttons cannot be pressed.
- **Connect to Node** asks for the RPC endpoint and the RPC credentials of your node, see [Connect it to your node](#connect-it-to-your-node). The node has to answer and to be on the right network. The page also has the network, which takes a restart.
- **Connect to Nostr** asks for the relays, one address on a line. A connection is opened to each of them and closed again; nothing is sent to a relay. Nostr answers if one relay does, and after *Connect* Black Hole posts to all the relays that you named, also to those that did not answer this time.
- **Connect to Coordinator** asks for the address of the coordinator of your coinjoins, and asks the coordinator for its rounds. While a coinjoin is under way the coordinator is not changed: stop the coinjoin first. The page also has the highest mining fee rate and the lowest number of inputs that you accept of a round, which take a restart, and the dust forfeit limit of the wallet, see [Coinjoin](#coinjoin).

A node, relays or a coordinator that were given on the command line are shown and asked, and cannot be changed on the page. When a setting was changed that takes a restart, the settings say so and offer the restart.

**The marks.** A *Connect to …* box has an orange check mark if you have connected Black Hole to it and it works at the moment, and an orange X if you have and it does not. A box without a mark is one that you have not connected to, or a Tor that is not used. Whether the connections work is found out while the settings are in sight, and not otherwise: Tor and the node are looked at every few seconds, and the relays and the coordinator are asked when the settings open and then once a minute, the way *Verify Connection* asks them. Right after the start, while Tor is still connecting, nothing is told yet about Tor and about what is reached through it.

**The wallet.** There is one wallet at most, so it is either recovered or created. Once it is there, the box that made it is *Configure Wallet*, with a check mark: the lower one if the wallet was created, the upper one if it was recovered. The other box stays what it was and can no longer be pressed. *Configure Wallet* has *Resync Wallet* and *Wallet Keys*, the extended public keys of the wallet. The wallet file tells whether the wallet was recovered; a file from before it did counts as created.

### The first start

As long as there is no wallet, the settings are the welcome. They cannot be put away, and the columns are set up from left to right: the boxes of a column can be pressed once the columns before it are done, and the column whose turn it is stands out. A column is done once you have connected on its page, which takes that Tor has connected to the Tor network, that the node answered, that a relay answered, that the coordinator answered. A Tor that is not used is done as it is. The wallet comes last, so there is no wallet before the four were connected.

Black Hole keeps note of what you have connected to (`ConfiguredConnections` in the config file), so a start without a wallet goes on where you were. A column stays done if what you connected to stops working: its box has the X then, and its page is where to connect again. A data folder that has a wallet needs no note: there, what the settings name counts as connected.

## The window

The title bar lists the pages of the wallet as tabs, once the wallet is decrypted:

- **History**: the transactions of the wallet. The wallet opens with it.
- **Coins**: the coins of the wallet. Tick coins in the list and press *Coinjoin*: everything that is done with the ticked coins starts in the dialog that it opens, and so does receiving. The dialog has a tab for every protocol, and it shows the main screen of the tab that is open:
  - *Payjoin*, which the dialog opens with. Payjoin itself holds its place and does nothing yet; its main screen has the ordinary payment and *Receive*. A payment takes the address, the amount and the fee rate, and *Send* shows the transaction; nothing is sent before you confirm it there. The fee rate starts as what your node estimates for a confirmation within two blocks, and you can type another one. What you typed is still there when you open the dialog again, until the payment is sent. *Receive* opens the addresses that were generated and have not been paid to yet, with *New Address* for another one; a right click on an address copies it.
  - *WabiSabi*: *Play* starts one round with the ticked coins, see [Coinjoin](#coinjoin). Its main screen has the payments that wait for a round to make them, and *Add Payment* queues one.
  - *ZeroLink*: *Play* starts one round with one ticked coin of a standard amount, see [ZeroLink rounds](#zerolink-rounds).
  - *Taker*: holds the place of the coinjoins that take the offers of market makers. There is none yet, and its *Play* cannot be pressed.
  - *Maker*: the [offer](#maker-offers) of the wallet as a market maker, which offers the ticked coins. *Bond* on this tab opens the [fidelity bonds](#fidelity-bonds) of the wallet, and a new bond locks the ticked coins.

  Below the tabs is the box of the coinjoins, with *Play* and *Stop*: it shows the tab that is open. A wallet has one coinjoin at a time, and the box is where it is watched and stopped: while one is under way the box shows it, whatever tab is open, so that you can pay or receive while a round goes on. The coinjoin goes on when the dialog is closed, and the box shows it when the dialog is opened again.

  No coin is ticked until you tick it, and the ticks are not kept: after a start of Black Hole none is ticked. A payment or a bond that was sent unticks what was ticked for it. While the wallet still processes the blocks of your node, *Receive* is all that works in the dialog.
- **Order Book**: the market makers of JoinMarket-style coinjoins with what they offer. It shows the offer of this wallet while it has one, and nobody else's: the wallet does not look for makers yet.

To the right of the tabs the title bar tells the fee rate, how many blocks the wallet has processed and the balance. In a window that is too narrow for all of it these give way, the fee rate first and the balance last, so that the tabs and the buttons stay. The gear at the right end opens [the settings](#the-settings).

## What was removed from Wasabi Wallet

Windows and macOS support, SegWit v0 addresses, hardware wallets, passphrases, BIP39 seed phrases, fiat exchange rates, the Branch and Bound search for changeless transactions, the dust attack limit, the Scheme scripting language, multiple wallets, confirmation time estimations, labels, the "ready to sign" signalling of coinjoins, the coinjoin strategies, the stop coinjoin threshold, the automatic start of coinjoins and their going on from round to round, the random and score-driven choice of the coins for a round with its isolation of non-private coins, the privacy progress with its ring, the anonymity score and its target, the sorting of the coins into private, semi-private and non-private ones, the privacy warnings of the transaction preview, compact block filter synchronization together with the peer to peer network stack, and every third party service (fee and exchange rate providers, block explorers, update checks).

## Requirements

- Linux on x64 or arm64.
- A Bitcoin node with the RPC interface enabled that broadcasts privately: Bitcoin Core 31 or later with `server=1` and `privatebroadcast=1`, and with a way to Tor or I2P, which the private broadcast goes through. See [Private broadcast](#private-broadcast). The node must have the blocks since the creation of the wallet, so a pruned node only works while it still has them.
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

*Connect to Node* of [the settings](#the-settings) asks for the two. *Verify Connection* there checks them with the node. *Connect* does too and, if the node answers and is on the right network, works with it right away.

The same settings are `BitcoinRpcEndPoint` and `BitcoinRpcCredentialString` in `Config.json`.

Recovering a wallet scans every block since the activation of Taproot unless you tell the height the wallet was created at (*Advanced Recovery Options*). On mainnet the full scan takes hours.

A wallet watches 210 unused addresses after the last one that was paid to (its gap limit). A recovered wallet does not find a payment whose address follows more unused addresses than that until the gap limit is raised, in *Advanced Recovery Options* or later with *Resync Wallet*, which is behind *Configure Wallet* in the settings. Addresses stay unused when they were generated with *New Address* and never paid to. Coinjoin rounds that fail leave change addresses unused too, but they do not take the wallet beyond its gap limit, see [Coinjoin](#coinjoin).

## Private broadcast

Black Hole hands every transaction of the wallet to your node and takes the node to broadcast it privately. Bitcoin Core does that since version 31 with `privatebroadcast=1` in its `bitcoin.conf`, for which the node needs a way to Tor (`proxy=` or `onion=`) or to I2P (`i2psam=`). Such a node does not announce the transaction to the nodes that it is connected to, which would tell them that the transaction came from yours. It sends it over a few short-lived connections through Tor or I2P instead, and takes it into its own mempool only when it hears it back from the network, like a transaction of anybody else.

Black Hole does not check that your node does this, and it cannot ask the node for it: it assumes it. A node without the setting announces the transaction itself, as its own.

What follows from the way such a node works:

- **A transaction is on its way for some seconds.** The node needs them to open its connections and to hear the transaction back. The wallet lists the transaction at once.
- **A payment from the change of a payment that was just sent waits for that one.** Until the node has heard a transaction back it knows nothing of what the transaction pays out, and it refuses a transaction that spends from it. Black Hole hands the second transaction over again every second until the node takes it. A node that has not heard the first one back after a minute does not get through to Tor or I2P; Black Hole says so then, and the second payment is to be sent again later.
- **A speed-up or a cancellation stops what it replaces.** If the node is still sending the transaction that is replaced, Black Hole tells it to stop, so that it does not go on sending both.
- **The node keeps what it is sending in its memory.** A transaction that it has not heard back when it is restarted is gone, and Black Hole goes on listing it as pending. Cancelling it in the *History*, or a speed-up that replaces it, puts something on its way again.

## Data

Black Hole keeps its data in `~/.blackhole/client`. The folder button in the top right corner of the window shows that folder in the file manager; the wallet, the config file and the logs are in it. It does not read or change the data of a Wasabi Wallet installation (`~/.walletwasabi`), and its Tor listens on other ports (37250 and 37251), so both can run on the same computer.

Started with `--loglevel=debug`, Black Hole writes into `Logs.txt` what the wallet asks the node for while it synchronizes: every block when it is asked for, when it arrives and when it is processed. That tells what a wallet that seems to be stuck is waiting for. The file starts anew when it reaches 10 MB, which is about 25,000 blocks; with `--loglevel=trace` it has no limit.

## Coinjoin

*Connect to Coordinator* of [the settings](#the-settings) sets the coordinator, without a restart. The coordinator of this repository (`WalletWasabi.Coordinator`) speaks the same protocol as the wallet. The coordinators of Wasabi Wallet expect the "ready to sign" message that Black Hole does not send, so do not count on them.

Coinjoins are started by hand. Tick coins on the *Coins* tab, press *Coinjoin* and open the *WabiSabi* tab: *Play* starts one round with the ticked coins, and when the round is over the coinjoin is stopped; the box tells how the round ended. The round gets the coins that are ticked at the moment you press *Play*, wherever they come from and however often they have been in a coinjoin, but ten at most: if more are ticked, the ten largest go into the round and the others stay ticked for a later one. A ticked coin also stays out if it cannot be registered at that time, for example because it is unconfirmed or banned by the coordinator, or if the round does not take its amount. While the round is under way its coins carry the coinjoin sign in the list, and the ticks are free for something else: what you tick or untick then does not change the round. The coins that a coinjoin gives you are new coins and unticked: tick them to submit them into another round. With no coin ticked, *Play* tells that no coin is ticked and nothing else happens. Nor does the wallet call a coin private, semi-private or non-private, and the preview of a transaction does not judge what the transaction gives away: which coins it spends together, how many payments it makes, how much of the balance it takes and whether it has change is yours to decide. The preview only has a sign, with what it means, when the transaction spends coins that are unconfirmed or in a coinjoin round, because of what that may do to the transaction.

Every round gets addresses that no round had before. A round that fails has shown the addresses of your outputs to the coordinator and, once its transaction was up for signing, to everybody in it; an address that came back in a later round would tell that the same wallet was in both. So the addresses of a failed round are left behind: the blame round that follows a round that not everybody signed gets new ones, and so does the next coinjoin after a round whose transaction could not be broadcast. The wallet file knows that an address is taken before the coordinator gets to see it, so closing the wallet in the middle of a round does not bring it back either. A wallet that is recovered from its backup has no such record: nothing on the chain tells which addresses a failed round took, so it may give them out again. Only the address of a payment that you queued for a coinjoin stays the same from round to round, because it is not the wallet's to change.

There is one exception, for the sake of the gap limit. A round that fails leaves up to ten change addresses unused, and a wallet that is recovered from its backup watches 210 unused change addresses after the last one that was paid to. So once 200 change addresses in a row have gone to rounds that failed, the rounds after them share the last ten of the 210: these ten come back from round to round. That ends when something is paid to one of them, by a round that succeeds or by the change of a payment. The wallet counts anew from the address that was paid to, and every round gets new addresses again. While rounds have had all ten, the change of a payment goes to the last of them, since an address beyond the 210 is one that a recovered wallet would not find.

The lists show every coin with its *anonymity points*. An output of a transaction is *matched* if another output of the same transaction has the same amount. A transaction is worth as many points as it has matched outputs, but not more than it has inputs that were matched outputs of the transactions that created them: the lower of the two numbers counts. The matched outputs of the transaction carry these points for as long as they are unspent; its other outputs, such as change, carry none. Nothing is inherited or averaged: the points of a coin do not depend on the points of the coins that paid for it, and they are gone when the coin is spent. A first coinjoin of coins that were never matched is therefore worth 0 points, and so is an ordinary payment. To count the inputs the wallet asks your node for the transactions that created them, also for the inputs of others. A coin shows *?* until the node has answered; a pruned node may no longer have the blocks that it takes.

Matched outputs are also what the wallet takes as unlinked. A coin that one of your transactions makes counts as linked to the coins that the transaction spent, unless it is a matched output of that transaction; change, whose amount occurs once, stays linked. When the wallet picks the coins of a payment out of the ticked ones, it takes confirmed coins before unconfirmed ones, then as few unlinked coins as the amount needs, then the coins closest to the amount. The preview of a payment does not warn about the coins that it spends together: they are among the ones that you ticked. The points of a coin do not make the wallet prefer it or hold it back.

A coinjoin splits your coins into standard amounts. What is left and is too small for an output of its own goes to the miners. *Dust forfeit limit* on the *Connect to Coordinator* page of the settings limits that: if a coinjoin would leave more than the limit, the leftover is added to one of your outputs instead. You keep the money, but that output has a non-standard amount and gains less privacy than the others. The limit is 0.00005 BTC unless you change it, and it cannot be set above 0.0001 BTC. Because of this limit the wallet also joins rounds whose smallest output is large ("whale rounds"), which Wasabi Wallet refuses from 0.0001 BTC on: what such a round cannot put into an output is kept the same way. If a round cannot keep the limit at all, the wallet gives up that round instead of signing it.

## ZeroLink rounds

A ZeroLink round is a coinjoin of the strictest kind: every participant puts in one coin, all the coins hold the same amount, and every participant gets one output, all of the same amount again. There is no change, and nothing in the transaction tells which inputs belong to one wallet, because no wallet has more than one. The rounds use the credentials and the phases of WabiSabi, not the blind signatures that the ZeroLink rounds of Wasabi Wallet 1 had, and the same coordinator holds them.

- **Input.** The amount of a round is a standard denomination: one of the amounts that WabiSabi coinjoins make their outputs of. In satoshis that is a power of 2, a power of 3 or twice that, or 1, 2 or 5 times a power of 10, for example 0.1 BTC, 0.01594323 BTC or 0.00131072 BTC. A round takes Taproot coins of exactly its amount.
- **Output.** Every output holds the denomination less the fee of a transaction with one input and one output: 111 vB at the fee rate of the round, rounded up to a whole satoshi. That is 57.5 vB for the input, 43 vB for the output and 10.5 vB for what a transaction has besides those. The inputs of a round are signed with the short signature of Taproot, 64 bytes, which is what makes an input 57.5 vB and every input look like the others; the coordinator takes no other signature. At 5 sat/vB a coin of 0.1 BTC comes out as 0.09999445 BTC. A round has that overhead once, so its transaction pays a little more than the fee rate: with n participants (n − 1) × 10.5 vB more, about 9% of the fee. In return the amount of the outputs is known before the round starts.
- **Which rounds there are.** The coordinator holds ZeroLink rounds for the denominations that `ZeroLinkDenominations` lists in its `Config.json`, for example `"ZeroLinkDenominations": ["0.1", "0.01"]`, one round of each at a time. The list is empty unless you fill it, and a coin of a denomination that is not listed finds no round. The coordinator does not start with a list that it cannot read, or with an amount in it that is not a standard denomination. The numbers of inputs and the timeouts of a ZeroLink round are the ones of the other rounds of the coordinator. A denomination that the fee would eat, or leave dust of, has no round until the fee rate comes down.
- **Taking part.** Tick a coin of a standard amount on the *Coins* tab, press *Coinjoin*, open the *ZeroLink* tab and press *Play*. One coin goes into one round: if several ticked coins have rounds, a coin of the largest amount goes, and the others stay ticked. With no ticked coin of a standard amount the box says so at once. If the coordinator has no round of the amount, the box says after ten minutes that no round was found. A wallet has one coinjoin at a time: while a round is under way the box shows that round with its *Stop*, whatever tab is open, so another round cannot be started until it is over. In the daemon, `startzerolink` starts a ZeroLink round with one of the coins that were chosen with `selectforcoinjoin`.
- **What the wallet checks.** Before it signs, the wallet checks the transaction against these rules: every input holds the denomination, every output holds the denomination less the fee, there are no more outputs than inputs and nothing else, and the fee is paid. The coordinator adds no output of its own. If a participant registers an input and then no output, the transaction has one output less; that participant does not sign, and the others go on to a blame round of the same denomination.
- **Afterwards.** What a ZeroLink round gives is not a standard amount, as the fee is missing, so the coin cannot go into another ZeroLink round as it is. A WabiSabi coinjoin makes standard amounts of it again. All the outputs of a ZeroLink round are matched outputs, so the transaction is worth as many anonymity points as it has inputs that were matched outputs before, see [Coinjoin](#coinjoin).

## Fidelity bonds

A fidelity bond is coins that are locked until the chain reaches a block height. A maker of JoinMarket-style coinjoins shows one to prove that it has something to lose, which makes it costly to pose as many makers. *Bond* on the *Maker* tab of the dialog that *Coinjoin* on the *Coins* tab opens creates, lists and redeems such bonds. Black Hole does not use them for anything yet: it has no coinjoin of that kind so far, and it makes no proof of a bond and shows none to anyone. The bonds are not the ones of JoinMarket, which does not understand them: they are Taproot outputs, and they unlock at a block height instead of a date.

- **Format.** A bond is a Taproot output that can be spent by one script only: `<unlock height> OP_CHECKLOCKTIMEVERIFY OP_DROP <public key> OP_CHECKSIG`. Its internal key is the point that BIP341 names for outputs without a key path (`50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0`). Nobody has the private key of that point, so whoever is shown the script can check that there is no way around the timelock. On the chain a bond looks like any other Taproot output until it is redeemed.
- **Unlock height.** You name the exact block height. The wallet tells the day that the chain is expected to get there, at a block every ten minutes; it is the blocks that decide, not the calendar. A bond can be redeemed once the chain has reached the height: the transaction that redeems it is valid from the next block on. The height has to be ahead of the chain, and no more than 525,600 blocks ahead, which is about ten years. Locked coins cannot be spent before that by anyone, and there is no way to undo it.
- **Keys.** The key of a bond is `m/86'/0'/0'/2/h` on mainnet, where `h` is the unlock height. It is a key of the account of the wallet, and every height has its own.
- **Creating.** Tick the coins on the *Coins* tab, then *Coinjoin*, the *Maker* tab, *Bond* and *New Bond*: name the height and the amount. The bond locks the ticked coins, or as much of them as you say. The transaction is previewed and sent like a payment. Every height has one address, and it can be paid from another wallet too; such a bond is listed once its transaction is confirmed. Every payment to the address is a bond of its own, and several small bonds count for less than one large one, so pay to it once.
- **Balance and history.** The coins in a bond belong to the wallet and are part of its balance, though it cannot spend them yet. The balance at the top of the window is everything that the wallet owns, in one amount; *Bond* lists what is locked. In the history, locking and unlocking have icons of their own, and their amount is what they changed in the balance: the mining fee, when the coins came from the wallet or go back to it. The details of the transaction tell the amount that was locked or unlocked.
- **Bond value.** The list tells what a bond counts for by the formula of JoinMarket, with the time counted in blocks: `(amount × (e^(0.015 × years) − 1))^1.3`, with the amount in satoshis and the years from the block that confirmed the bond to its unlock height, at 52,594.92 blocks a year. The number has no unit; it compares bonds. After the unlock height it wears off.
- **Redeeming.** Once the chain has reached the unlock height, *Redeem* pays a bond back to the wallet, to a new address of it, less the mining fee.
- **Recovery.** There are too many heights to watch the addresses of all of them, so the wallet watches the ones that it gave out, and the wallet file remembers them. The recovery words do not tell them. A wallet that is recovered, or told to *Resync Wallet*, therefore looks for a bond at every height that it could have chosen, from the first block that it goes through to ten years above the chain. Getting ready for that takes about a minute before the first block is processed. This finds the bonds that are in the blocks. A bond address from before the recovery that is paid after it, or a bond that another copy of the wallet made, is found by resyncing again.
- **Privacy.** A bond that is shown to others is public, and so is the history of the coins in it.

## Maker offers

A market maker of JoinMarket-style coinjoins lets a taker put its coins into the coinjoin of the taker. *Maker* makes the offer of this wallet: tick one or two coins on the *Coins* tab, press *Coinjoin*, open the *Maker* tab and name three numbers.

- **Minimum fee rate**, in sat/vB: the lowest mining fee rate of a coinjoin that your coins go into.
- **Bid**, in sats per anonymity point: what you pay for a point.
- **Ask**, in sats per anonymity point: what you want for a point. It cannot be lower than the bid.

What the two prices are for: a coinjoin destroys the anonymity points of the coins that go into it, and the coins that come out of it have the points of the new transaction. So the coinjoin of a taker swaps the points of your coins for others. If you have more points afterwards, you pay the taker the bid for each of them; if you have fewer, the taker pays you the ask for each.

Black Hole does nothing with an offer yet except post it: it does not look for the offers of others, and it does not answer a taker. No coinjoin of this kind exists so far: the *Taker* tab, next to *Maker*, holds its place and does nothing.

- **Where it goes.** *Post* sends the offer to the Nostr relays that you connected to with *Connect to Nostr* of [the settings](#the-settings). You name them there, one address on a line. A new installation has `wss://relay.primal.net`, `wss://relay.damus.io` and `wss://nos.lol`; with no relay in the list no offer can be posted. Like the other settings they can be given at the start instead, with `--nostrrelays=wss://one.example;wss://two.example`. The relays are reached through Tor, unless Tor is turned off. Nothing is sent to a relay before you post an offer. This and the coordinator of your WabiSabi coinjoins are the only servers that Black Hole talks to besides your node.
- **What it tells.** The offer is an event of the kind for the discovery of coinjoins (15750, a [draft](https://github.com/nostr-protocol/nips/pull/384)) with the tags `type` = `BlackHoleJoinMarket` and `network` = `mainnet`, `signet` or `regtest`. Its content is JSON, for example `{"minFeeRate":2.5,"bid":100,"ask":150,"liquidity":50000000}`: the fee rate, the two prices, and what the ticked coins hold together, in sats. The third tag of that kind, `endpoint`, is left out: there is no place yet where a taker reaches a maker.
- **What it does not tell.** Which coins they are, and what each of them holds. Whoever reads the relays can still search the chain for coins of that amount, most easily when the offer has one coin. Every offer is signed with a new key, so two offers cannot be told to come from the same wallet by their keys.
- **How long it stands.** Until you take it back with *Withdraw offer*, until its coins are spent, or until you close Black Hole. Readers of that kind of event take one that is older than 15 minutes for gone, so a standing offer is posted again every 10 minutes. The kind is replaceable, which means that a relay keeps only the newest event of a key: an offer is changed by posting it again, and taken back by an event that tells nothing. An offer is not stored. After a restart there is none until you post one.
- **Coins.** The offer holds the coins that are ticked when you post it, two at most. With no coin ticked, an offer that stands keeps its coins, so that you can change its prices without ticking its coins again. A coin of the offer that is spent leaves the offer, which is posted again with what is left. The offer keeps no coin from being spent or coinjoined.
- **When no relay can be reached.** An offer that no relay took is not made, and the page tells why. If a standing offer cannot be posted again, its row on the *Order Book* tab shows a warning, and it is tried again every minute.

## License

[MIT](LICENSE.md), as Wasabi Wallet.
