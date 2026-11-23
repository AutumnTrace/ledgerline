namespace Ledgerline.QueryPlans;

/// <summary>
/// Deterministic seed data, generated inside PostgreSQL with generate_series so it takes seconds, not minutes.
/// Ids are md5-derived UUIDs, so every run produces the same rows. Player p-1 is a heavy player
/// (one in twenty game transactions) to give the statement queries a deep history.
/// </summary>
internal static class SeedSql
{
    public const int Players = 50_000;
    public const int GameTransactions = 1_000_000;
    public const int Withdrawals = 100_000;
    public const int OutboxMessages = 1_000_000;
    public const int PendingOutbox = 250;

    public static readonly (string Step, string Sql)[] Steps =
    [
        ("Operator accounts", """
            INSERT INTO accounts (id, code, type, owner_id, currency, balance_minor, version, opened_at) VALUES
              (md5('house')::uuid,    'house:gaming:EUR',               'HouseGaming', NULL,            'EUR', NULL, 0, now() - interval '400 days'),
              (md5('clearing')::uuid, 'psp:northwind-pay:clearing:EUR', 'PspClearing', 'northwind-pay', 'EUR', NULL, 0, now() - interval '400 days'),
              (md5('suspense')::uuid, 'psp:northwind-pay:suspense:EUR', 'PspSuspense', 'northwind-pay', 'EUR', NULL, 0, now() - interval '400 days')
            """),
        ("Player wallets", $"""
            INSERT INTO accounts (id, code, type, owner_id, currency, balance_minor, version, opened_at)
            SELECT md5('player-' || p)::uuid, 'player:p-' || p || ':cash:EUR', 'PlayerCash', 'p-' || p, 'EUR', 0, 0, now() - interval '400 days'
            FROM generate_series(1, {Players}) AS p
            """),
        ("Deposit entries", $"""
            INSERT INTO journal_entries (id, type, idempotency_key, request_hash, transaction_id, player_id, round_id, reverses_entry_id, posted_at)
            SELECT md5('dep-' || p)::uuid, 'DepositSettled', 'deposit:northwind-pay:dep-' || p, repeat('0', 64), 'dep-' || p, 'p-' || p, NULL, NULL,
                   now() - interval '400 days'
            FROM generate_series(1, {Players}) AS p
            """),
        ("Deposit postings", $"""
            INSERT INTO postings (entry_id, account_id, currency, amount_minor, balance_after_minor, entry_type, posted_at)
            SELECT entry_id, account_id, 'EUR', amount, balance_after, 'DepositSettled', now() - interval '400 days'
            FROM (
              SELECT p, 0 AS leg, md5('dep-' || p)::uuid AS entry_id, md5('clearing')::uuid AS account_id,
                     -(CASE WHEN p = 1 THEN 100000000 ELSE 1000000 END)::bigint AS amount, NULL::bigint AS balance_after
              FROM generate_series(1, {Players}) AS p
              UNION ALL
              SELECT p, 1, md5('dep-' || p)::uuid, md5('player-' || p)::uuid,
                     (CASE WHEN p = 1 THEN 100000000 ELSE 1000000 END)::bigint, (CASE WHEN p = 1 THEN 100000000 ELSE 1000000 END)::bigint
              FROM generate_series(1, {Players}) AS p
            ) AS legs
            ORDER BY p, leg
            """),
        ("Game entries", $"""
            INSERT INTO journal_entries (id, type, idempotency_key, request_hash, transaction_id, player_id, round_id, reverses_entry_id, posted_at)
            SELECT md5('tx-' || g)::uuid,
                   CASE WHEN g % 3 = 0 THEN 'WinCredit' ELSE 'BetDebit' END,
                   'wallet:tx-' || g, repeat('0', 64), 'tx-' || g,
                   'p-' || (CASE WHEN g % 20 = 0 THEN 1 ELSE g % {Players} + 1 END),
                   'r-' || (g / 2), NULL,
                   now() - interval '365 days' + g * interval '30 seconds'
            FROM generate_series(1, {GameTransactions}) AS g
            """),
        ("Game postings (with running balances)", $"""
            WITH game AS MATERIALIZED (
              SELECT g,
                     CASE WHEN g % 20 = 0 THEN 1 ELSE g % {Players} + 1 END AS p,
                     CASE WHEN g % 3 = 0 THEN 100 + g % 2000 ELSE -(100 + g % 900) END::bigint AS amount,
                     CASE WHEN g % 3 = 0 THEN 'WinCredit' ELSE 'BetDebit' END AS entry_type,
                     now() - interval '365 days' + g * interval '30 seconds' AS posted_at
              FROM generate_series(1, {GameTransactions}) AS g
            )
            INSERT INTO postings (entry_id, account_id, currency, amount_minor, balance_after_minor, entry_type, posted_at)
            SELECT entry_id, account_id, 'EUR', amount, balance_after, entry_type, posted_at
            FROM (
              SELECT g, 0 AS leg, md5('tx-' || g)::uuid AS entry_id, md5('player-' || p)::uuid AS account_id, amount,
                     (CASE WHEN p = 1 THEN 100000000 ELSE 1000000 END) + SUM(amount) OVER (PARTITION BY p ORDER BY g) AS balance_after,
                     entry_type, posted_at
              FROM game
              UNION ALL
              SELECT g, 1, md5('tx-' || g)::uuid, md5('house')::uuid, -amount, NULL, entry_type, posted_at
              FROM game
            ) AS legs
            ORDER BY g, leg
            """),
        ("Wallet balances", """
            UPDATE accounts AS a SET balance_minor = s.total, version = s.postings
            FROM (SELECT account_id, SUM(amount_minor) AS total, COUNT(*) AS postings FROM postings GROUP BY account_id) AS s
            WHERE a.id = s.account_id AND a.type = 'PlayerCash'
            """),
        ("Withdrawals (1% still open)", $"""
            INSERT INTO withdrawals (id, withdrawal_id, player_id, provider, currency, amount_minor, status, requested_at, resolved_at,
                                     reserve_entry_id, resolution_entry_id, reversal_reason, version)
            SELECT md5('wd-' || w)::uuid, 'wd-' || w, 'p-' || (w % {Players} + 1), 'northwind-pay', 'EUR', 1000 + w % 5000,
                   CASE WHEN w % 100 = 0 THEN 'Reserved' WHEN w % 7 = 0 THEN 'Reversed' ELSE 'Completed' END,
                   now() - interval '200 days' + w * interval '2 minutes',
                   CASE WHEN w % 100 = 0 THEN NULL ELSE now() - interval '200 days' + w * interval '2 minutes' + interval '1 hour' END,
                   md5('wd-reserve-' || w)::uuid, NULL, NULL, CASE WHEN w % 100 = 0 THEN 0 ELSE 1 END
            FROM generate_series(1, {Withdrawals}) AS w
            """),
        ("Outbox (published history plus a backlog)", $$"""
            INSERT INTO outbox_messages (message_id, topic, message_key, message_type, payload, trace_parent, occurred_at, processed_at, attempts, last_error)
            SELECT md5('msg-' || m)::uuid, 'ledger.entries.v1', 'p-' || (m % {{Players}} + 1), 'ledger.entry.posted.v1', '{"seed":true}', NULL,
                   now() - interval '1 day' + m * interval '50 milliseconds',
                   CASE WHEN m > {{OutboxMessages - PendingOutbox}} THEN NULL ELSE now() END,
                   CASE WHEN m > {{OutboxMessages - PendingOutbox}} THEN 0 ELSE 1 END,
                   NULL
            FROM generate_series(1, {{OutboxMessages}}) AS m
            """),
    ];
}
