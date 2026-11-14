Wallet diagnostic
----------

Toolset to observe the internal state of the wallet. The scripts ask the RPC server of Black Hole, so it must be enabled (without user name and password) and the wallet must be loaded.

## Keys state generation

Create a visual representation from a dump of the keys of the wallet.

### How to run it

Run Black Hole and open the wallet.

Next open a terminal and enter:

```bash
dotnet fsi keygraph.fsx
```

### The result

![walletkeys.png](img/walletkeys.png)

### Dependencies

None

## CoinGraph generation

Create a visual representation from a dump of the coins of the wallet.

### How to run it

Run Black Hole and open the wallet.

Next open a terminal and enter:

```bash
dotnet fsi txgraph.fsx <initial-txid> | dot -Tpng | feh  -
```

## The result

![txgraph.png](img/txgraph.png)

## Dependencies

- graphviz, required for dot
- feh
