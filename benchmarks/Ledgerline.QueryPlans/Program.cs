// Seeds a PostgreSQL database with a realistic ledger and records EXPLAIN (ANALYZE, BUFFERS) for the
// queries the service actually runs: the read-side SQL constants verbatim, and the SQL EF Core generates
// for the write side. CI runs it against a fresh PostgreSQL service container and publishes the report.
//
//   dotnet run -c Release --project benchmarks/Ledgerline.QueryPlans -- "<connection string>" [report.md]

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ledgerline.Infrastructure.Persistence;
using Ledgerline.Infrastructure.ReadModel;
using Ledgerline.QueryPlans;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var connectionString = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("LEDGER_QUERYPLANS_DB")
    ?? throw new ArgumentException("Pass a PostgreSQL connection string as the first argument.");
var reportPath = args.Length > 1 ? args[1] : "query-plans.md";
var invariant = CultureInfo.InvariantCulture;

var options = new DbContextOptionsBuilder<LedgerDbContext>();
DatabaseSetup.Configure(options, DatabaseProvider.Postgres, connectionString);
await using var db = new LedgerDbContext(options.Options);
await db.Database.MigrateAsync();

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var seedTimer = Stopwatch.StartNew();
foreach (var (step, sql) in SeedSql.Steps)
{
    var stepTimer = Stopwatch.StartNew();
    await using var command = dataSource.CreateCommand(sql);
    command.CommandTimeout = 600;
    var rows = await command.ExecuteNonQueryAsync();
    Console.WriteLine($"Seed: {step}: {rows:N0} rows in {stepTimer.Elapsed.TotalSeconds:0.0}s");
}

await using (var vacuum = dataSource.CreateCommand("VACUUM ANALYZE"))
{
    vacuum.CommandTimeout = 600;
    await vacuum.ExecuteNonQueryAsync();
}

seedTimer.Stop();

// Seed ids are md5-derived inside PostgreSQL; ask it for the ones the queries need.
var heavy = await ScalarAsync<Guid>("SELECT md5('player-1')::uuid");
var house = await ScalarAsync<Guid>("SELECT md5('house')::uuid");
var deepCursor = await ScalarAsync<long>($"SELECT id FROM postings WHERE account_id = '{heavy}' ORDER BY id DESC OFFSET 25000 LIMIT 1");
var anyEntry = await ScalarAsync<Guid>("SELECT md5('tx-424242')::uuid");

// EF Core queries, rendered with the provider's own SQL generator (values inlined as constants).
var idempotencyLookup = db.JournalEntries.Include(e => e.Postings).Where(e => e.IdempotencyKey == "wallet:tx-424242").Take(2).ToQueryString();
var outboxBatch = db.OutboxMessages.Where(m => m.ProcessedAt == null).OrderBy(m => m.Id).Take(100).ToQueryString();

var queries = new List<PlanQuery>
{
    new("Idempotency check (write side, every command)", "Unique index `ix_journal_entries_idempotency_key`, then `ix_postings_entry_id` for the replay body.",
        idempotencyLookup, []),
    new("Statement, first page (player with ~50k postings)", "Covering index `ix_postings_account_statement`, walked backwards: an index-only scan that stops after 51 rows.",
        LedgerSql.StatementFirstPage, [new("AccountId", heavy), new("Take", 51)]),
    new("Statement, page 500 by keyset cursor", "Same index; `id < @Before` seeks straight to the page, so depth does not matter.",
        LedgerSql.StatementNextPage, [new("AccountId", heavy), new("Before", deepCursor), new("Take", 51)]),
    new("Statement, page 500 by OFFSET (for contrast, not used)", "What keyset pagination avoids: the index still has to produce and discard 25,000 rows.",
        LedgerSql.StatementFirstPage.Replace("LIMIT @Take", "OFFSET 25000 LIMIT @Take", StringComparison.Ordinal), [new("AccountId", heavy), new("Take", 51)]),
    new("Player wallets (balance check)", "Partial index `ix_accounts_owner_id_type` (owned accounts only); stored balances, no aggregation.",
        LedgerSql.PlayerWallets, [new("PlayerId", "p-1")]),
    new("Entry with its postings", "`ix_postings_entry_id`, then the accounts primary key.",
        LedgerSql.EntryPostings, [new("EntryId", anyEntry)]),
    new("Open withdrawal holds", "Partial index `ix_withdrawals_pending` holds only the 1% still Reserved.",
        LedgerSql.PendingWithdrawals, [new("Take", 100)]),
    new("Outbox, next batch (dispatcher poll)", "Partial index `ix_outbox_messages_pending` holds only the unpublished backlog, not the million published rows.",
        outboxBatch, []),
    new("Derived balance of a hot operator account", "Index-only scan over every house posting (~1M). The honest cost of not storing operator balances; see the roadmap.",
        LedgerSql.Account, [new("AccountId", house)]),
};

var counts = await ScalarAsync<string>("""
    SELECT format('%s accounts, %s journal entries, %s postings, %s withdrawals (%s open), %s outbox messages (%s pending)',
      to_char((SELECT count(*) FROM accounts), 'FM9,999,999'),
      to_char((SELECT count(*) FROM journal_entries), 'FM9,999,999'),
      to_char((SELECT count(*) FROM postings), 'FM9,999,999'),
      to_char((SELECT count(*) FROM withdrawals), 'FM9,999,999'),
      to_char((SELECT count(*) FROM withdrawals WHERE status = 'Reserved'), 'FM9,999,999'),
      to_char((SELECT count(*) FROM outbox_messages), 'FM9,999,999'),
      to_char((SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL), 'FM9,999,999'))
    """);
var version = await ScalarAsync<string>("SHOW server_version");

var report = new StringBuilder();
report.AppendLine("# Query plans").AppendLine();
report.AppendLine(invariant, $"PostgreSQL {version}, seeded with {counts} in {seedTimer.Elapsed.TotalSeconds:0}s, then `VACUUM ANALYZE`.");
report.AppendLine("Each query runs once to warm the cache; the plan below is from the second run of `EXPLAIN (ANALYZE, BUFFERS)`.");
report.AppendLine("Generated by `benchmarks/Ledgerline.QueryPlans`.").AppendLine();
report.AppendLine("| Query | Plan | Execution time | Buffers |").AppendLine("|---|---|---:|---|");

var details = new StringBuilder();
foreach (var query in queries)
{
    await ExplainAsync(query);
    var plan = await ExplainAsync(query);
    var top = Regex.Replace(plan[0], @"\s+\(cost=.*$", string.Empty).Trim().TrimStart('-', '>', ' ');
    var executionTime = plan.Select(l => Regex.Match(l, @"Execution Time: ([\d.]+) ms")).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "?";
    var buffers = plan.Select(l => Regex.Match(l, @"Buffers: (shared [^\r\n]+)")).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "none";
    report.AppendLine(invariant, $"| {query.Title} | `{top}` | {executionTime} ms | {buffers} |");

    details.AppendLine(invariant, $"## {query.Title}").AppendLine();
    details.AppendLine(query.Why).AppendLine();
    details.AppendLine("<details><summary>SQL</summary>").AppendLine();
    details.AppendLine("```sql").AppendLine(query.Sql.Trim()).AppendLine("```").AppendLine();
    if (query.Parameters.Count > 0)
    {
        details.AppendLine(invariant, $"Parameters: {string.Join(", ", query.Parameters.Select(p => $"`@{p.Name}` = `{p.Value}`"))}").AppendLine();
    }

    details.AppendLine("</details>").AppendLine();
    details.AppendLine("```text").AppendLine(string.Join(Environment.NewLine, plan)).AppendLine("```").AppendLine();
}

report.AppendLine().Append(details);
await File.WriteAllTextAsync(reportPath, report.ToString());
Console.WriteLine(report.ToString());
return 0;

async Task<List<string>> ExplainAsync(PlanQuery query)
{
    await using var command = dataSource.CreateCommand("EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) " + query.Sql);
    foreach (var parameter in query.Parameters)
    {
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
    }

    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        lines.Add(reader.GetString(0));
    }

    return lines;
}

async Task<T> ScalarAsync<T>(string sql)
{
    await using var command = dataSource.CreateCommand(sql);
    return (T)(await command.ExecuteScalarAsync())!;
}

internal sealed record PlanParameter(string Name, object Value);

internal sealed record PlanQuery(string Title, string Why, string Sql, IReadOnlyList<PlanParameter> Parameters);
