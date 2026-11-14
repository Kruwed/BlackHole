# Wasabi CLI

A bash script for effortless interaction with the Wasabi RPC Server.

USAGE:

```bash
$ ./wcli.sh command [ARGS,...]
```

There is a single wallet, so the commands do not take a wallet name. Create it once with `createwallet` (it prints the SLIP-39 shares of the backup) or recover it with `recoverwallet`, then open it with `loadwallet`.

The supported RPC commands are listed in the [documentation](https://docs.wasabiwallet.io/using-wasabi/RPC.html).

## Examples

```bash
$ ./wcli.sh getstatus

{
  "torStatus": "Running",
  "backendStatus": "Connected",
  "bestBlockchainHeight": "2432219",
  "bestBlockchainHash": "0000000000000013081887ac34a2bc356a99a3979a808a4e5d63358412cecc68",
  "filtersCount": 5001,
  "filtersLeft": 0,
  "network": "Main",
  "peers": [
    {
      "isConnected": true,
      "lastSeen": 1794210150,
      "endpoint": "[::ffff:148.251.1.20]:18343",
      "userAgent": "/Satoshi:31.1.0/"
    }
  ]
}
```

```bash
$ ./wcli.sh getwalletinfo

{
  "walletFile": "/home/ricardo/.walletwasabi/client/Wallets/Wallet.json",
  "state": "Started",
  "masterKeyFingerprint": "3442193e",
  "anonScoreTarget": 5,
  "isAutoCoinjoin": true,
  "isRedCoinIsolation": false,
  "accounts": [
    {
      "name": "taproot",
      "publicKey": "tpubDDXRVY4eRY4p4iqLUQNQokx89YLZEpTyA8UdLVYRMV8HacVdwHRi1TWKMSY3kh8WpNBA4kB4Xet3hCxxVvSw83DZ5fhqiSysSsYCWJ7k79E",
      "keyPath": "m/86'/1'/0'"
    }
  ],
  "balance": 198738301,
  "coinjoinStatus": "Idle"
}
```

```bash
$ ./wcli.sh listkeys | head -5

fullkeypath     internal  keystate  handedout  scriptpubkey                                                        pubkey                                                              address
86'/1'/0'/1/0   true      2         false      1 cabac13996a24ccecf98761c9b0edf1764bd0cea42925c82cbdaf8658b61b07d  0361b19484ccee6f5d9842be7a461f11e021e3dba709195466d08ad8cdf872014e  tb1pe2avzwvk5fxvanucwcwfkrklzajt6r82g2f9eqktmtuxtzmpkp7s9f78va
86'/1'/0'/0/0   false     2         true       1 15fd41acdeafcf4628c048e9cfcfdbb92af61002de8fbfd493460042b1e7a2c0  0383f00c97a27a6015d53acd68686aeaef0f26756cd3b24fef767420566e3de154  tb1pzh75rtx74l85v2xqfr5uln7mhy40vyqzm68ml4yngcqy9v085tqqvq73sa
86'/1'/0'/1/1   true      0         false      1 9450706c1949cffff61606fb1a7d6735c80b2793e5f4312a45c692cbd98c8624  037ef89bbd48be5013a888a2b72576cd35699fddea4b19cfeec67e4a91bfa7d624  tb1pj3g8qmqef88llaskqma35lt8xhyqkfunuh6rz2j9c6fvhkvvscjq6tklq6
86'/1'/0'/0/1   false     0         true       1 208ecde3fffd5d51474e1414ffd3e92460962cfe78e9b0b4a7a37cb1a32e2fc2  03958b96aabd7c8336c86a954011a89ff69ca8298d7a0ad947afae601423b98184  tb1pyz8vmclll4w4z36wzs20l5lfy3sfvt870r5mpd985d7trgew9lpq0gzy53
```

`handedout` tells whether `getnewaddress` (or the Receive screen) already gave the address out. An address is given out only once, whether or not it was paid.

```bash
$ ./wcli.sh gethistory | head -10

datetime                   height   amount     tx                                                                islikelycoinjoin
2018-06-27T17:39:40+00:00  1326503  1000       b2cc6a4f437d915abfd7b851d382102037c3abe7932138fcfb92bc4b5eddd08c  false
2018-07-26T05:25:59+00:00  1355500  50000      6144f487705096397a39d2f0e7e23dd3e9c13d71e9cfc74b04e633eaf2f32a7a  false
2018-07-26T05:25:59+00:00  1355500  -555       ce41d4a5fe0c0da955210bf8722e43af8f580f87a005d7429272bfeb376426ea  false
2018-07-26T05:25:59+00:00  1355500  -720       5429cf93723f37af21f3c4ff5bb11e04af6e79a31aedcd4bbe93e9ae88989e1b  false
2018-07-26T05:25:59+00:00  1355500  -890       405822d6e574019c9f286b0f548d3824a3e305031556fa62a537b13745a0b0a0  false
2018-08-02T17:33:52+00:00  1356647  -17856     a98cca3e7e920bc97b85c17eb256bcaeabf7dec84491a8dfda8850ec4ec9bebf  false
2018-08-02T17:33:52+00:00  1356647  -13764     146eec8341caa0ad7bc63d325e3257e8e88fe4042df25bb9bf25e387909ebe8d  false
2018-08-02T17:52:24+00:00  1356649  219540     07d6708563d3ab4dba975a3058ec6d907bf6c52a1b2061b694548b77bf359dfd  false
2018-08-02T17:52:24+00:00  1356649  -13764     b84e225062daeee4064dd7d13396da0b26bbe644d74b9960f9094aa5424f1a32  false
```

```bash
$ ./wcli.sh getnewaddress

{
  "address": "tb1ptug7cqc4pfqvayvpcqx3wrgw39avz8tvcuwudeuryjmr8fdxxthq5ayh9k",
  "keyPath": "86'/1'/0'/0/58",
  "publicKey": "02ba26c1496eee949e6be86e9ed58fa49bd40d099d8e12e5a22f1e0da3eb71663a",
  "scriptPubKey": "51205f11ec03150a40ce9181c00d170d0e897ac11d6cc71dc6e78324b633a5a632ee"
}
```
