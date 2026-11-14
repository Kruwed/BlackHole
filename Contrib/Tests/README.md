# Black Hole Regtest CoinJoin Testing Script

This bash script automates the setup and testing of the CoinJoin functionality of Black Hole in a local Bitcoin regtest environment. It creates a complete testing environment with a Bitcoin node, a coordinator, and multiple wallet clients performing simultaneous CoinJoins.

## Overview

The script performs the following operations:

1. Starts a Bitcoin Core node in regtest mode
2. Generates initial blocks to create spendable coins
3. Starts a coordinator
4. Starts the wallet client daemons
5. Creates multiple wallets and funds them
6. Initiates CoinJoin operations across all wallets
7. Monitors the coordinator logs for successful CoinJoin completion

## Prerequisites

## Configuration

The script uses the following default configuration (editable at the top of the script):

- `BITCOIN_DATADIR` - `/tmp/bitcoin-regtest` - Bitcoin regtest data directory
- `BLACKHOLE_DATADIR` - `/tmp/blackhole` - Black Hole data directory
- `BITCOIN_RPC_PORT` - `18443` - Bitcoin RPC port
- `BITCOIN_P2P_PORT` - `18444` - Bitcoin P2P port
- `COORDINATOR_PORT` - `37126` - Coordinator port
- `WALLET_RPC_PORT` - `37128` - RPC port of the first wallet client
- `NUM_WALLETS` - `5` - Number of wallets to create
- `ADDRESSES_PER_WALLET` - `4` - Addresses per wallet to fund
- `TEST_TIMEOUT` - `600` - Timeout in seconds (10 minutes)

## Usage

```bash
./regtest-coinjoin-test.sh
```

## References

- Black Hole GitHub: https://github.com/Kruwed/BlackHole
- Bitcoin Core regtest documentation
- RPC commands of the wallet: [Contrib/CLI](../CLI/README.md)
