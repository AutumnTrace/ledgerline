## Summary

<!-- What changes and why? Link the issue if there is one. -->

## Changes

-

## Testing

<!-- What did you run, and what does it prove? -->

## Checklist

- [ ] `dotnet test --filter "Category!=Integration"` passes locally
- [ ] Money still moves only through balanced journal entries; new invariants have unit or property tests
- [ ] New commands are idempotent (key + fingerprint) and covered by a replay test
- [ ] Schema changes come with a migration (`dotnet ef migrations add <Name> --project src/Ledgerline.Infrastructure`)
- [ ] New read queries live in `LedgerSql`, run on SQLite and PostgreSQL, and have an index story in `docs/data-model.md`
- [ ] Public API changes are reflected in the README and `docs/errors.md`
- [ ] `CHANGELOG.md` updated under **Unreleased**
