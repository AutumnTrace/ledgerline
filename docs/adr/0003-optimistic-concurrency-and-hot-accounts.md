# 3. Optimistic concurrency on wallets; no stored balance on operator accounts

- Status: Accepted
- Date: 2026-11-23

## Context

A player wallet must never go negative, even when a game server fires several bets at once or a
withdrawal races a bet. The check ("is there enough?") and the write must be atomic.

At the same time, almost every entry touches an operator account: every bet and win moves money to or
from `house:gaming:EUR`. If that account had a stored balance protected the same way, all transactions
in a currency would serialise on one row, and throughput would be bounded by how fast that single row
can be locked, updated and committed.

## Decision

**Wallets** (`PlayerCash`, `PlayerBonus`) store `balance_minor` and a `version` column that EF Core uses
as a concurrency token. A posting checks the invariant in the domain, updates the balance and bumps the
version; the `UPDATE ... WHERE id = @id AND version = @expected` either succeeds or affects no rows.
A check constraint (`balance_minor >= 0`) backs up the domain rule.

When a command loses the race, the store raises `TransientWriteConflictException` and the
`CommandDispatcher` re-runs the **whole command** in a fresh DI scope (new `DbContext`, fresh reads),
with bounded, jittered exponential backoff (Polly, default 5 retries from 10 ms). Duplicate-key
violations (two first-time account opens, two copies of one request) and PostgreSQL serialization
failures or deadlocks are handled the same way. Retrying is safe because every command is idempotent:
if an earlier attempt committed, the retry finds it by its idempotency key and returns it as a replay.
When retries run out the API returns `409 write-conflict` with `Retry-After`, and the client retries with
the same transaction id.

**Operator accounts** (`HouseGaming`, `BonusPool`, `PspClearing`, `PspSuspense`) store no balance
(`balance_minor IS NULL`) and are never updated after they are opened. Their balance is the sum of
their postings, computed on read through the covering statement index. No invariant needs their balance
at write time, so nothing is lost by not storing it.

## Consequences

- Bets on different wallets never contend, even though they all credit the same house account.
- One hot wallet sheds load instead of queueing. In the integration suite, 100 parallel 5.00 bets
  against a 100.00 wallet on PostgreSQL produced exactly 20 postings and 80 insufficient-funds
  rejections, with 580 conflict retries along the way and none exhausted. The balance-after chain
  has no gaps.
- The version column is portable: the same code runs on SQLite (local) and PostgreSQL.
- Reading an operator account's balance costs an aggregate over its postings. That is fine for
  dashboards, but it grows with history; the roadmap has periodic balance snapshots.

## Alternatives considered

- **`SELECT ... FOR UPDATE` (pessimistic locking).** Simple and correct, but holds row locks across the
  read-check-write window and is PostgreSQL-specific in practice. Under contention, waiting is worse
  than a fast, bounded retry for a seamless-wallet API with tight timeouts.
- **PostgreSQL `xmin` as the concurrency token.** No extra column, but ties the model to PostgreSQL
  and is invisible in the schema. An explicit `version` doubles as a posting counter for the wallet.
- **Atomic `UPDATE ... SET balance = balance + @delta` on operator accounts.** Keeps a stored house
  balance, but every transaction in the currency then serialises on that row lock.
- **Sharding the house account** into N sub-accounts. A valid scale-out step if stored operator
  balances become necessary; not needed while they can be derived.
