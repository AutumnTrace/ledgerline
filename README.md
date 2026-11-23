# 📒 Ledgerline

**Double-entry wallet ledger for player balances: DDD and CQRS on EF Core and PostgreSQL, optimistic concurrency, idempotent postings and a transactional outbox to Kafka.**

> [!NOTE]
> Work in progress. This commit contains the domain core: money in integer minor units, the chart of accounts,
> balanced journal entries and the withdrawal lifecycle, with unit and property-based tests.

```bash
dotnet test --filter "Category!=Integration"
```

## License

[MIT](LICENSE) © Mina Rotari
