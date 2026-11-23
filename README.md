# 📒 Ledgerline

**Double-entry wallet ledger for player balances: DDD and CQRS on EF Core and PostgreSQL, optimistic concurrency, idempotent postings and a transactional outbox to Kafka.**

> [!NOTE]
> Work in progress. The write side is in place: idempotent commands for deposits, seamless-wallet bets,
> wins and rollbacks, withdrawal holds, bonuses and wallet transfers, on EF Core with SQLite or PostgreSQL.

```bash
dotnet run --project src/Ledgerline.Api --launch-profile http   # http://localhost:5180/scalar
dotnet test --filter "Category!=Integration"
```

## License

[MIT](LICENSE) © Mina Rotari
