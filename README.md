# REPEATER9000

REPEATER9000 is an AddOn for the NinjaTrader 8 software that copies a leader account's orders to follower accounts, organized by group.

Multi-leader to multi-follower simultaneous copying capability. 

Ditch the expensive and redundant subscriptions with Replikanto and TradeSyncer for something **faster and free.**

## Copier Core Functionality

- Market, limit, stop-market, stop-limit, and MIT orders

- Order price and quantity changes

- Cancels, fills, and rejections

- ATM stop/target brackets using follower-side OCO groups

- The copier mimics the leader's native ATM order management: When the leader's ATM adjusts a stop, target, or bracket quantity, the corresponding follower order is amended. Each follower keeps its own OCO identifier, so a filled target cancels its paired stop at the broker/account level.

## Requirements

- NinjaTrader 8
- Connected leader and follower accounts
- Appropriate permissions and risk controls for every account

## Installation

1. Download REPEATER9000.cs
2. Add file to Documents/NinjaTrader8/bin/Custom/AddOns
3. Open **NinjaScript Editor** in NinjaTrader 8.
4. Double click to view [`REPEATER9000.cs`](REPEATER9000.cs) AddOn source in Editor. 
5. Compile using F12. 
6. Open **Control Center → New → REPEATER9000**.

## Start-Up

1. Add follower accounts. (Use CTRL or SHIFT key to select and add multiple accounts at once)
2. Create a group and assign its leader account.
3. Assign followers to that group.
4. Confirm the configuration with simulated accounts first.
5. Enable copying only when ready.

Warning: The window can be closed to hide it but the copier remains active until NinjaTrader shuts down. 

Reopen from **Control Center → New → REPEATER9000**.

## Safety Behavior

- Copying is always disabled upon startup.
- Follower route changes blocked while any copied orders are active.
- **Flatten ALL** disables copying and requests flattening for all configured follower accounts currently engaged in an order.
- Errors are written to NinjaTrader's trace output.

## Advanced Feature: Latency Probe

Enable **Latency Probe** to record detailed follower order timing data to:

`Documents/NinjaTrader 8/Repeater9000LatencyProbe.csv`

## Important

This software submits and modifies REAL orders. Test with Sim101 and simulation follower-accounts before using live capital. 

You are entirely responsible for account permissions, order sizing, connectivity, and risk management.

Any catastrophic losses are solely attributed to the user. I 

This is not to be taken as financial advice. 
