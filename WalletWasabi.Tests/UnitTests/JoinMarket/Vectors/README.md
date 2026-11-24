# Expected results of the market for anonymity points

These files are the expected results of the design of the maker and taker market (prototype "Maker Order Book",
Black Hole 0.1.12). They were produced by the planner of the prototype and read off its page, and the tests in the
folder above replay them: `MarketRulesTests`, `DecomposerTests`, `TransactionPlannerTests`, `OrderBookRulesTests` and
`MakerSideRulesTests`.

**They are not to be edited.** A case that fails means that the code is wrong.

| Folder | What it holds |
| --- | --- |
| `rules/` | The rule of the points by hand: output values, and what they are worth. |
| `planner/` | 70 whole plans of a taker's coinjoin, from no maker to the whole sample book of 250, and `INDEX.json` with a line for each. |
| `search/` | 180 cases for the three searches that the planner is made of. |
| `order-book/` | The ranking of the sample book, what the filters cut, and what the taker's strategy triangle sets. |
| `maker/` | What the maker's strategy triangle sets, what a selection of coins is worth as an offer, and the figures of a fidelity bond. |

Amounts are satoshis in every field whose name ends in `Sats` and for every bare output value, and BTC with decimals
in fields whose names end in `Btc`. Fee rates are in sat/vB and the rates of a point in sats per point. Whole numbers
are matched exactly, and numbers that are not whole within a relative tolerance of 1e-9. Lists are matched in their
order: the order in which a plan picks its outputs is a part of what is expected.

The cases of `order-book/` and `maker/` are about the sample book and the sample wallet of the design, which the
application has as well: `WalletWasabi/JoinMarket/Sample/makers.json` and `utxos.json`.
