# 4. A transactional outbox (and an inbox) instead of dual writes

- Status: Accepted
- Date: 2026-11-23

## Context

Other services need to know when money moves: reconciliation, risk, reporting, CRM. Publishing to
Kafka directly from the request handler is a dual write: commit the database, then publish (or the
reverse). Either order has a failure window. Commit-then-publish loses the event if the process dies or
the broker is down between the two; publish-then-commit announces an entry that may roll back. Neither
is acceptable for money.

## Decision

**Outbox.** When the store saves a unit of work, it converts the domain events raised by the aggregates
(`JournalEntryPosted`) into rows in `outbox_messages`, inside the same transaction as the entry. The
message is serialised once, as a versioned integration contract (`LedgerEntryPostedV1`), separate from
the domain event so the domain can change without breaking consumers.

**Relay.** `OutboxDispatcherService` polls for unpublished rows in id order (a partial index on
`processed_at IS NULL` keeps that query proportional to the backlog), publishes them, and marks the
acknowledged prefix processed. If message 3 of 5 fails, 1 and 2 are marked, 3 records the error and
attempt count, and 3 to 5 are retried on the next pass. On PostgreSQL the pass runs under
`pg_try_advisory_xact_lock`, so one replica relays at a time and per-key order holds.

**Kafka.** Topic `ledger.entries.v1`, keyed by player id (a player's entries stay in one partition, in
order). The producer is idempotent with `acks=all`. Headers carry `message-id`, `message-type`,
`content-type` and the W3C `traceparent`, so a trace runs from the HTTP request through the relay to the
consumer.

**Inbox.** Delivery is at-least-once: a crash after the broker acknowledged but before the row was
marked re-sends the message. Consumers deduplicate. The round-summary projector inserts
`(consumer, message_id)` into `inbox_messages` in the same transaction as its projection update; the
primary key turns a redelivery into a no-op. The Kafka consumer stores its offset only after that
transaction commits.

**Local mode.** With `Messaging:Transport=InMemory` the relay publishes to an in-process channel and a
log line, so `dotnet run` exercises the same outbox, relay and inbox code with no broker.

## Consequences

- No lost or phantom events: an entry and its message commit or roll back together.
- Messages may be delivered more than once and, after a failed publish in the middle of a batch,
  slightly out of order across a retry. Consumers must be idempotent; the inbox shows how.
- Publishing lags commits by up to the polling interval (250 ms by default). Backlog and lag are
  exported as metrics (`ledger.outbox.pending`, `ledger.outbox.lag`) and turn readiness to Degraded past
  a threshold.
- The outbox table grows; published rows need a retention job (on the roadmap).

## Alternatives considered

- **Publish after commit, best effort.** Loses events on crashes and broker outages.
- **Kafka transactions / exactly-once.** Covers Kafka-to-Kafka pipelines, not a PostgreSQL commit.
- **Change data capture (Debezium on the WAL).** Removes polling and is a good next step at scale, but
  adds a Kafka Connect cluster to operate and couples consumers to table shapes unless an outbox table
  is used anyway (the outbox event router pattern). The polling relay keeps the service self-contained.
