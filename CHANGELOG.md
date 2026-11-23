# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- Dependabot keeps the .NET SDK and runtime images on the major version the projects target

## [0.1.0] - 2026-11-23

First release.

### Added

- **Double-entry core**: `Money` in integer minor units with ISO 4217 currencies, a chart of accounts (player cash and bonus wallets, house, bonus pool, PSP clearing and suspense), and `JournalEntry`, which refuses to exist unless its postings sum to zero per currency. Domain events for every posted entry.
- **Idempotent commands** for deposits, seamless-wallet bets, wins and rollbacks, the withdrawal hold (reserve, complete, reverse), bonus grants and cash/bonus transfers. A replay returns the original result with its original balances; reusing a transaction id for a different request is a 409 (#1)
- **Optimistic concurrency** on wallets and withdrawals with a bounded, jittered retry in a fresh unit of work per attempt; operator accounts store no balance, so they never become a hot row (#1)
- **REST API** under `/v1` with RFC 9457 problem details, .NET 10 validation, OpenAPI 3.1 and Scalar (#1)
- **CQRS read side** on Dapper: wallet balances, accounts with derived operator balances, keyset-paginated statements with an opaque cursor, entries, open withdrawal holds, and a trial balance (#2)
- **Transactional outbox** to Kafka (`ledger.entries.v1`, keyed by player, W3C trace context in headers), with an ordered, at-least-once relay and a PostgreSQL advisory lock for a single active relay (#3)
- **Inbox-deduplicated consumer** that projects per-round totals (`GET /v1/rounds/{roundId}`) (#3)
- In-memory transport so `dotnet run` works with no broker; SQLite for local runs, PostgreSQL 18 with EF Core migrations for production (#1, #3)
- **Observability**: Serilog, OpenTelemetry traces and metrics (postings, commands, conflict retries, outbox backlog, lag and delivery delay), OTLP and Azure Monitor exporters behind configuration, JSON readiness report (#1, #3)
- Chiseled, non-root multi-arch Docker image and a compose stack with PostgreSQL 18 and Kafka 4 (#3, #5)
- **Tests**: domain unit tests, CsCheck property tests over random command histories, API tests, and a Testcontainers suite on PostgreSQL and Kafka covering concurrency, migrations, the read side and the outbox (#1 to #4)
- **CI**: unit and integration tests, merged coverage summary, `EXPLAIN (ANALYZE, BUFFERS)` report over two million seeded postings, image build, end-to-end smoke test of the compose stack, CodeQL. Image published to GHCR on release tags (#3, #4, #5)
- Architecture decision records, data model and error catalogue (#5)

[Unreleased]: https://github.com/AutumnTrace/ledgerline/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/AutumnTrace/ledgerline/releases/tag/v0.1.0
