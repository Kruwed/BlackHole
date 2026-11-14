## Release Highlights

#### A fork of Wasabi Wallet
#### Only your own Bitcoin node
#### One Taproot wallet, backed up with SLIP39

## Release Summary
Black Hole is a fork of Wasabi Wallet that keeps its Tor integration and its coinjoins and gets everything else from your own Bitcoin node.

### A fork of Wasabi Wallet
Black Hole builds on the Tor integration and the coinjoin implementation of Wasabi Wallet. Hardware wallets, fiat exchange rates, labels, the scripting language, multiple wallets and the Windows and macOS versions were removed.

### Only your own Bitcoin node
The wallet reads the blocks, the unconfirmed transactions and the fee estimations from the RPC interface of your Bitcoin node and broadcasts its transactions through it. Apart from the coinjoin coordinator that you choose, nobody else is contacted.

### One Taproot wallet, backed up with SLIP39
There is a single wallet. It generates Taproot addresses only, and its backup is SLIP39: 20 recovery words by default, or several shares with a threshold if you choose to split it. The password protects the wallet file on this computer; the backup alone recovers the wallet.
