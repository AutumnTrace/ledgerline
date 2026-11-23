# 5. CQRS with a small hand-rolled dispatcher and a Dapper read side

- Status: Accepted
- Date: 2026-11-23

## Context

Writes and reads in a ledger have opposite needs. Writes load a few aggregates, enforce invariants,
retry on conflicts and commit atomically; EF Core's change tracking and concurrency tokens fit well.
Reads are statements, balances and reports over large tables, where the shape of the SQL and the index
it hits decide the latency, and loading aggregates through change tracking is wasted work.

Commands also need cross-cutting behaviour (tracing, metrics, logging, and above all the retry loop
with a fresh unit of work per attempt), which is the classic reason to reach for a mediator library.

## Decision

**Write side.** Each command is a record implementing `ICommand` (idempotency key + fingerprint) with one
`ICommandHandler<T>`. A shared template (`PostingCommandHandler<T>`) handles the replay check, posting and
commit once; handlers only decide which accounts move and by how much. `CommandDispatcher` is a single
class of about 140 lines: it opens a DI scope per attempt, runs the handler inside a Polly retry pipeline, and records
the activity, metrics and logs. Handlers are registered explicitly, with no assembly scanning.

**Read side.** Each query is a record with one `IQueryHandler<TQuery, TResult>` in the infrastructure
layer. Handlers run hand-written SQL (all of it in `LedgerSql`) through Dapper on their own connections,
never touching the EF Core model. The SQL is portable between SQLite and PostgreSQL; Dapper type
handlers make GUIDs and timestamps read the same way EF Core writes them on both. An optional
`ConnectionStrings:LedgerRead` points the read side at a replica.

The read model is mostly the write tables plus indexes designed for the reads (a covering index for
statements, partial indexes for open holds and the outbox backlog). The round summary is a true
asynchronous projection, fed from Kafka through the inbox.

## Consequences

- The retry-with-fresh-scope behaviour, which is the critical part, is explicit and tested rather than
  hidden in a pipeline behaviour.
- Read queries are visible, reviewable SQL. CI runs `EXPLAIN (ANALYZE, BUFFERS)` on exactly these
  strings against two million seeded postings, so an index regression shows up in the job summary.
- Two data-access styles in one codebase, and SQL that must stay portable across two providers (both are
  covered by tests: SQLite in the unit suite, PostgreSQL in the integration suite).
- No mediator library, so no notification fan-out or open-generic pipeline behaviours. They are not needed.

## Alternatives considered

- **MediatR.** The de facto choice, but recent versions are commercially licensed, and here it would
  mostly hide the one behaviour that matters (retry with a fresh scope) behind a generic pipeline.
- **EF Core for reads too** (`AsNoTracking`, projections). Workable, but the generated SQL is harder to
  control for keyset pagination and covering indexes, and reads would share the write model's shape.
- **A separate read database fed by events for everything.** Strong isolation, but eventual consistency
  for balances is wrong for a wallet: a player must see their bet reflected immediately. Only the round
  summary, where staleness is harmless, is built that way.
