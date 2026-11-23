# 1. A double-entry journal instead of balance columns

- Status: Accepted
- Date: 2026-11-23

## Context

The obvious wallet design is a `balance` column per player and `UPDATE wallets SET balance = balance - 25`
per bet. It is fast and simple, and it works until someone asks a question it cannot answer: where did
this money come from? Why does the sum of player balances not match what the payment providers say we
hold? Which bet did this rollback cancel? With only a mutable number, history lives in logs (if anywhere)
and every discrepancy becomes an investigation.

Money in a gaming platform moves between parties that all need accounting: players (cash and bonus),
the house (bets in, wins out), payment providers (deposits in, payouts out, payouts in flight) and the
promotions budget. Regulators, finance and the reconciliation process all need the same answers from
the same data.

## Decision

Every movement is a **journal entry** with two or more **postings**. A posting moves a signed amount into
(+) or out of (-) one account. The `JournalEntry` aggregate refuses to exist unless, for each currency
in it, the postings sum to exactly zero. Postings are immutable; mistakes are corrected with new,
reversing entries (a bet rollback is the bet's mirror image, linked through `reverses_entry_id`).

Balances are a consequence of postings, not the source of truth. Player wallets keep a stored balance
as a cache that is updated in the same transaction as the posting, so the overdraft check and the
balance read are a single-row operation (see [ADR 3](0003-optimistic-concurrency-and-hot-accounts.md)).

Signs follow the account's own point of view: a deposit is `+X` on the player's cash wallet and `-X` on
the provider's clearing account. Because every entry nets to zero, **the sum of all balances in a
currency is always zero**, which gives a free, always-on integrity check (`GET /v1/ledger/trial-balance`).

## Consequences

- Every cent has a provenance: an entry with a type, the caller's transaction id and the counter-account.
- Invariants are testable as properties: the CsCheck suite generates random command histories and
  checks that entries balance, totals are zero and stored balances equal their postings.
- Reconciliation has something to reconcile against: PSP clearing and suspense accounts mirror what a
  provider's settlement file should contain.
- Writes are heavier: one entry plus at least two posting rows instead of one `UPDATE`.
- Reporting on operator accounts means aggregating postings; see the roadmap item on balance snapshots.

## Alternatives considered

- **Balance column only.** Rejected: no audit trail, no way to prove totals, corrections destroy history.
- **Balance column plus an append-only transactions table.** Better, but nothing forces the two to agree
  and there is no counter-party, so "where did it go" still has no answer.
- **Event sourcing the wallet aggregate.** Gives history, but balance queries need snapshots or
  projections from day one, and the accounting rules (balanced entries) still have to be built on top.
  The journal is already an append-only log with a well-understood shape.
