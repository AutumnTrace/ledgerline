namespace Ledgerline.Infrastructure.ReadModel;

/// <summary>
/// Every read-side statement, in one place. Portable SQL that runs unchanged on PostgreSQL and SQLite.
/// The query-plans tool runs <c>EXPLAIN (ANALYZE, BUFFERS)</c> on exactly these strings.
/// </summary>
internal static class LedgerSql
{
    /// <summary>Served by <c>ix_accounts_owner_id_type</c> (partial: only owned accounts).</summary>
    public const string PlayerWallets = """
        SELECT id AS account_id, code, type, owner_id, currency, balance_minor, version, opened_at
        FROM accounts
        WHERE owner_id = @PlayerId AND type IN ('PlayerCash', 'PlayerBonus')
        ORDER BY currency, CASE type WHEN 'PlayerCash' THEN 0 ELSE 1 END
        """;

    /// <summary>
    /// Wallets return their stored balance. Operator accounts derive it from their postings; COALESCE only
    /// evaluates the subquery when the stored balance is NULL. The subquery is an index-only scan on
    /// <c>ix_postings_account_statement</c>.
    /// </summary>
    public const string Account = """
        SELECT a.id AS account_id, a.code, a.type, a.owner_id, a.currency,
               COALESCE(a.balance_minor,
                        (SELECT CAST(COALESCE(SUM(p.amount_minor), 0) AS BIGINT) FROM postings p WHERE p.account_id = a.id)) AS balance_minor,
               a.version, a.opened_at
        FROM accounts a
        WHERE a.id = @AccountId
        """;

    /// <summary>Keyset pagination, first page: newest postings first, straight off the covering index.</summary>
    public const string StatementFirstPage = """
        SELECT id AS posting_id, entry_id, entry_type, amount_minor, balance_after_minor, posted_at
        FROM postings
        WHERE account_id = @AccountId
        ORDER BY id DESC
        LIMIT @Take
        """;

    /// <summary>
    /// Keyset pagination, later pages: <c>id &lt; @Before</c> seeks into the index, so page 1000 costs the
    /// same as page 1 (unlike OFFSET, which reads and throws away every earlier row).
    /// </summary>
    public const string StatementNextPage = """
        SELECT id AS posting_id, entry_id, entry_type, amount_minor, balance_after_minor, posted_at
        FROM postings
        WHERE account_id = @AccountId AND id < @Before
        ORDER BY id DESC
        LIMIT @Take
        """;

    public const string Entry = """
        SELECT id AS entry_id, type, transaction_id, player_id, round_id, reverses_entry_id, posted_at
        FROM journal_entries
        WHERE id = @EntryId
        """;

    /// <summary>Uses <c>ix_postings_entry_id</c>, then the accounts primary key.</summary>
    public const string EntryPostings = """
        SELECT p.id AS posting_id, p.account_id, a.code AS account_code, a.type AS account_type,
               p.currency, p.amount_minor, p.balance_after_minor
        FROM postings p
        JOIN accounts a ON a.id = p.account_id
        WHERE p.entry_id = @EntryId
        ORDER BY p.id
        """;

    /// <summary>Oldest open holds first. Reads only the partial index <c>ix_withdrawals_pending</c>.</summary>
    public const string PendingWithdrawals = """
        SELECT withdrawal_id, player_id, provider, currency, amount_minor, requested_at
        FROM withdrawals
        WHERE status = 'Reserved'
        ORDER BY requested_at
        LIMIT @Take
        """;

    /// <summary>A full aggregate over the journal: a reporting query, not a hot path.</summary>
    public const string TrialBalance = """
        SELECT p.currency, a.type AS account_type, COUNT(DISTINCT a.id) AS accounts,
               CAST(SUM(p.amount_minor) AS BIGINT) AS balance_minor
        FROM postings p
        JOIN accounts a ON a.id = p.account_id
        GROUP BY p.currency, a.type
        ORDER BY p.currency, a.type
        """;
}
