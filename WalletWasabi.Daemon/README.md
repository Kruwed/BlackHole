Black Hole Daemon
=================

Black Hole daemon is a _headless_ Black Hole wallet designed to minimize the usage of resources (CPU, GPU, Memory, Bandwidth) with the goal of
making it more suitable for running all the time in the background.

## Configuration

All configuration options available via `Config.json` file are also available as command line arguments and environment variables:

### Command Line and Environment variables

* Command line switches have the form `--switch_name=value` where _switch_name_ is the same name that is used in the config file (case insensitive).
* Environment variables have the form `BLACKHOLE_SWITCHNAME` where _SWITCHNAME_ is the same name that is used in the config file.

A few examples:

| Config file                | Command line                | Environment variable                |
|----------------------------|-----------------------------|-------------------------------------|
| Network: "TestNet"         | --network=testnet           | BLACKHOLE_NETWORK=testnet           |
| JsonRpcServerEnabled: true | --jsonrpcserverenabled=true | BLACKHOLE_JSONRPCSERVERENABLED=true |
| UseTor: true               | --usetor=true               | BLACKHOLE_USETOR=true               |

### Values precedence

* **Values passed by command line arguments** have the highest precedence and override values in environment variables and those specified in config files.
* **Values stored in environment variables** have higher precedence than those in config file and lower precedence than the ones pass by command line.
* **Values stored in config file** have the lower precedence.

### Special values

There are a few special switches that are not present in the `Config.json` file and are only available using command line and/or variable environment:

* **LogLevel** to specify the level of detail used during logging
* **DataDir** to specify the path to the directory used during runtime. The default is `~/.blackhole/client`.
* **BlockOnly** to make the wallet look only at the blocks of the Bitcoin node and not at its unconfirmed transactions
* **LoadWallet** (`--loadwallet`, without a value) to open the wallet automatically after the start.

### Examples

Run the daemon on the testnet Bitcoin network with Tor disabled and accept JSON RPC calls. Store everything in `$HOME/temp/blackhole-1`.

```bash
$ blackholed --usetor=false --datadir="$HOME/temp/blackhole-1" --network=testnet --jsonrpcserverenabled=true --blockonly=true
```

Run the daemon on the testnet Bitcoin network.

```bash
$ BLACKHOLE_NETWORK=testnet blackholed
```

Run the daemon and open the wallet

```bash
$ blackholed --loadwallet
```

`blackholed` is the name of the daemon in the release packages. From the source code it is started with `dotnet run --project WalletWasabi.Daemon --` followed by the same switches.

### Version

```bash
$ blackholed --version
Black Hole Daemon 1.0.0
```

### Usage

To interact with the daemon, use its JSON RPC server, for example with the [wcli script](../Contrib/CLI).
