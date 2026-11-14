# Ports

A reference of common local ports used by Black Hole and related software.
The ports of Wasabi Wallet and of its ancestor HiddenWallet (3712x, 3715x) are chosen within a long range of unassigned IANA ports, based on [this](https://stackoverflow.com/a/28369841/2061103) statistic, but also checked against [Service Name and Transport Protocol Port Number Registry](https://www.iana.org/assignments/service-names-port-numbers/service-names-port-numbers.xhtml). Black Hole runs its Tor on ports of its own, so it can run next to Wasabi Wallet.

| Port  | Application                                             |
|-------|---------------------------------------------------------|
| 37126 | Coordinator on RegTest                                  |
| 37128 | Black Hole RPC Server (same as Wasabi Wallet)           |
| 37129 | Black Hole RPC Server published as a Tor onion service  |
| 37250 | Tor socks port used by Black Hole                       |
| 37251 | Tor control port used by Black Hole                     |
| 37252 | Tor socks port used by Black Hole with a custom Tor folder   |
| 37253 | Tor control port used by Black Hole with a custom Tor folder |
| 37255 | Tor socks port used by the coordinator                  |
| 37256 | Tor control port used by the coordinator                |
| 37150 | Tor socks port used by Wasabi Wallet                    |
| 37151 | Tor control port used by Wasabi Wallet                  |
| 9050  | Default Tor socks port                                  |
| 9051  | Default Tor control port                                |
| 9150  | Tor socks port used by Tor Browser                      |
| 9151  | Tor control port used by Tor Browser                    |
| 8332  | Bitcoin Core Mainnet RPC                                |
| 48332 | Bitcoin Core Testnet4 RPC                               |
| 38332 | Bitcoin Core Signet RPC                                 |
| 18443 | Bitcoin Core Regtest RPC                                |
