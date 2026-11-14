# Bitcoin Setup (RegTest)

## What is RegTest?

RegTest is a local network in which users can generate blocks and coins for testing. Running Black Hole in RegTest allows you to emulate network events and observe how Black Hole reacts. You don't need to download the blockchain.

## Bitcoin Core Setup

1. Install Bitcoin Core from the [Official Website](https://bitcoincore.org/en/download/) or [GitHub](https://github.com/bitcoin/bitcoin/releases/) on your computer. Verify the PGP signatures. Check the security advisories [here](https://bitcoincore.org/en/security-advisories/).
2. Start Bitcoin Core with: `bitcoin-qt -regtest` then select Bitcoin data directory or leave it as the default. If you use the `-datadir` parameter, make sure the directory exists.

    `-datadir` using example:

    ```
     ~/bitcoin-[version number]/bin/bitcoin-qt -regtest -txindex -datadir=$HOME/.bitcoin/
    ```

4. Go to Bitcoin data directory.

    There may be differences if you used the `-datadir` parameter before.

    Defaults:

    ```
    $HOME/.bitcoin/
    ```
4. Edit / Create a **bitcoin.conf** file and add these lines:
    ```C#
    regtest.server = 1
    regtest.listen = 1
    regtest.txindex = 1
    regtest.whitebind = 127.0.0.1:18444
    regtest.rpchost = 127.0.0.1
    regtest.rpcport = 18443
    regtest.rpcuser = rpcuser
    regtest.rpcpassword = rpcpassword
    regtest.disablewallet = 0
    regtest.softwareexpiry = 0
    regtest.listenonion = 0
    ```
5. Save it.
6. Close Bitcoin Core to confirm changes and open it again with: `bitcoin-qt -regtest`.
7. Do not worry about "Syncing Headers" just press the Hide button. Because you run on Regtest, no Mainnet blocks will be downloaded.
8. Go to menu *File / Create* and create a wallet with the name you prefer. Use the default options.
9. Go to menu *Window / Console*.
10. Generate a new address with:
`getnewaddress`
11. Generate the first 101 blocks with:
`generatetoaddress 101 <replace_new_address_here>`
12. Now you have your own Bitcoin blockchain. You can create transactions with the Send button and confirm with creating a new block:
`generatetoaddress 1 <replace_new_address_here>`

You can force rebuilding the txindex with the `-reindex` command line argument. Bitcoin Core needs to be running during the next steps.

# Black Hole Setup

Here you will have to build from source, follow [these instructions here](../README.md#build-and-run).

[Contrib/Tests/regtest-coinjoin-test.sh](../Contrib/Tests/regtest-coinjoin-test.sh) does everything below by itself with a coordinator and several wallets.

## Coordinator Setup

1. Go to `WalletWasabi.Coordinator` folder.
2. Open the command line and enter:
`dotnet run`
3. You will get some errors, but the data directory will be created.
4. Stop the application if it is still running with CTRL-C.
5. Go to the Coordinator folder:
    ```
    /home/{your username}/.blackhole/coordinator
    ```
6. Edit these lines in `Config.json`:
    ```
    "Network": "RegTest",
    "BitcoinRpcConnectionString": "rpcuser:rpcpassword",
    "StandardInputRegistrationTimeout": "0d 0h 2m 0s",
    "MinInputCountByRoundMultiplier": 0.02,
    ```
7. Go to WalletWasabi.Coordinator folder.
8. Open the command line and enter:
`dotnet run`
9. The coordinator is now running in RegTest network

## Client Setup

There is a single wallet in a data directory, and a coinjoin needs several participants, so every wallet runs in a client of its own with its own data directory.

1. Go to `WalletWasabi.Fluent.Desktop` folder.
2. Open the command line and run the first client with:
`dotnet run -- --network=regtest --datadir=$HOME/.blackhole/regtest1 --bitcoinrpccredentialstring=rpcuser:rpcpassword`
3. Create the wallet and write down its recovery words.
4. Generate a receive address in Black Hole, now go to Bitcoin Core to the Send tab.
5. Send 1 BTC to that address.
6. Do the same with a second client and `--datadir=$HOME/.blackhole/regtest2`.
7. Generate a block in Bitcoin Core so that the coins are confirmed:
`generatetoaddress 1 <replace_new_address_here>`
8. Now start the coinjoin in both wallets.
9. If you see `Waiting for confirmed funds` in the music box you can generate a block in Bitcoin Core to continue coinjoining:
`generatetoaddress 1 <replace_new_address_here>`

On RegTest the clients use the coordinator at `http://localhost:37126/` unless another one is set.
