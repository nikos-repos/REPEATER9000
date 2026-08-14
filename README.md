# repeater9000

repeater9000 is a ninjatrader 8 addon that copies orders from leader accounts to grouped follower accounts. it supports simultaneous copying from multiple leaders to multiple followers.

the addon replaces paid copier subscriptions such as replikanto and tradesyncer with a local copier.

## copier behavior

- copies market, limit, stop-market, stop-limit, and mit orders.
- copies order price and quantity changes.
- copies cancels, fills, and rejections.
- creates atm stop and target brackets with follower-side oco groups.
- mirrors native leader atm order management. when a leader atm adjusts a stop, target, or bracket quantity, repeater9000 amends the corresponding follower order. each follower keeps its own oco id, so a filled target cancels its paired stop at the broker or account level.

## requirements

- ninjatrader 8.
- connected leader and follower accounts.
- appropriate permissions and risk controls for each account.

## installation

1. download `REPEATER9000.cs`.
2. add it to `Documents/NinjaTrader8/bin/Custom/AddOns`.
3. open the ninjascript editor in ninjatrader 8.
4. open [`REPEATER9000.cs`](REPEATER9000.cs) in the editor.
5. compile with `F12`.
6. open `control center → new → repeater9000`.

## start-up

1. add follower accounts. use `ctrl` or `shift` to select multiple accounts.
2. create a group and assign its leader account.
3. assign followers to the group.
4. confirm the configuration with simulated accounts.
5. enable copying when ready.

the window can close without stopping the copier. the copier stays active until ninjatrader shuts down. reopen it from `control center → new → repeater9000`.

## safety behavior

- copying starts disabled.
- follower route changes are blocked while copied orders are active.
- `flatten all` disables copying and requests flattening for configured follower accounts with active orders.
- errors are written to ninjatrader trace output.

## latency probe

enable `latency probe` to record follower order timing data at:

`Documents/NinjaTrader 8/Repeater9000LatencyProbe.csv`

## operating boundary

this software submits and modifies real orders. test with `Sim101` and simulated follower accounts before using live capital.

the user is responsible for account permissions, order sizing, connectivity, and risk management. the author accepts no liability for use of this addon. this addon is not financial advice.
