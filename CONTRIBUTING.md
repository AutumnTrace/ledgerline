# Contributing

Thanks for taking a look. Ledgerline is small on purpose; changes should keep it correct first and fast second.

## Getting started

You need the .NET 10 SDK. Docker is only needed for the integration tests and the compose stack.

```bash
dotnet build
dotnet test --filter "Category!=Integration"      # fast suite: domain, property, SQLite, API (no Docker)
dotnet test tests/Ledgerline.IntegrationTests       # PostgreSQL 18 + Kafka 4 via Testcontainers (Docker)
dotnet run --project src/Ledgerline.Api --launch-profile http
```

## Ground rules for money

- Money moves only through `JournalEntry.Post`. If a change needs a balance to change any other way, the
  design is wrong.
- Amounts are `Money` (integer minor units plus an ISO 4217 currency). No `double`, no bare `decimal` amounts.
- Every command is idempotent: give it an idempotency key derived from the caller's id and a fingerprint
  of the fields that define it, and add a replay test.
- New invariants get a property test in `tests/Ledgerline.Tests/Properties` where it makes sense.

## Database changes

Entity configurations live in `src/Ledgerline.Infrastructure/Persistence/Configurations`. After changing
the model, add a PostgreSQL migration (the tool is pinned in `.config/dotnet-tools.json`):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Ledgerline.Infrastructure --output-dir Persistence/Migrations
```

`MigrationTests` fails if you forget. Read-side SQL lives in `LedgerSql` and must run on both SQLite and
PostgreSQL; if a query needs a new index, explain why in `docs/data-model.md`. The `query-plans` CI job
shows the plan it gets against two million postings.

## Commits and pull requests

- [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `test:`, `docs:`, `perf:`,
  `ci:`, `build:`, `chore:`.
- One coherent change per pull request, with a summary, the list of changes and how it was tested.
- CI must be green: build, unit tests, integration tests, coverage, query plans, the Docker image and the
  end-to-end smoke test against the compose stack.
- Significant design decisions get an ADR in `docs/adr`.
