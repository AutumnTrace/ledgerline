using Dapper;
using Ledgerline.Application.Abstractions;
using Ledgerline.Application.Queries;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Infrastructure.ReadModel;

internal sealed class GetPlayerWalletsHandler(ReadConnectionFactory connections) : IQueryHandler<GetPlayerWallets, IReadOnlyList<AccountView>>
{
    public async Task<IReadOnlyList<AccountView>> HandleAsync(GetPlayerWallets query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AccountRow>(new CommandDefinition(LedgerSql.PlayerWallets, new { query.PlayerId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToView())];
    }
}

internal sealed class GetAccountHandler(ReadConnectionFactory connections) : IQueryHandler<GetAccount, AccountView?>
{
    public async Task<AccountView?> HandleAsync(GetAccount query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(LedgerSql.Account, new { query.AccountId }, cancellationToken: cancellationToken));
        return row?.ToView();
    }
}

internal sealed class GetStatementHandler(ReadConnectionFactory connections) : IQueryHandler<GetStatement, StatementPage?>
{
    public async Task<StatementPage?> HandleAsync(GetStatement query, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > GetStatement.MaxLimit)
        {
            throw new LedgerValidationException("limit", $"limit must be between 1 and {GetStatement.MaxLimit}.");
        }

        long? before = query.Cursor is null ? null : StatementCursor.Decode(query.Cursor);

        await using var connection = await connections.OpenAsync(cancellationToken);
        var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            new CommandDefinition(LedgerSql.Account, new { query.AccountId }, cancellationToken: cancellationToken));
        if (account is null)
        {
            return null;
        }

        // Fetch one extra row to learn whether another page exists without a COUNT(*).
        var take = query.Limit + 1;
        var command = before is null
            ? new CommandDefinition(LedgerSql.StatementFirstPage, new { query.AccountId, Take = take }, cancellationToken: cancellationToken)
            : new CommandDefinition(LedgerSql.StatementNextPage, new { query.AccountId, Before = before.Value, Take = take }, cancellationToken: cancellationToken);
        var rows = (await connection.QueryAsync<StatementRow>(command)).ToList();

        var hasMore = rows.Count > query.Limit;
        var lines = rows
            .Take(query.Limit)
            .Select(r => new StatementLine(r.PostingId, r.EntryId, r.EntryType, r.AmountMinor, r.BalanceAfterMinor, r.PostedAt))
            .ToList();

        return new StatementPage(account.ToView(), lines, hasMore ? StatementCursor.Encode(lines[^1].PostingId) : null);
    }
}

internal sealed class GetEntryHandler(ReadConnectionFactory connections) : IQueryHandler<GetEntry, EntryView?>
{
    public async Task<EntryView?> HandleAsync(GetEntry query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var entry = await connection.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition(LedgerSql.Entry, new { query.EntryId }, cancellationToken: cancellationToken));
        if (entry is null)
        {
            return null;
        }

        var postings = await connection.QueryAsync<EntryPostingRow>(new CommandDefinition(LedgerSql.EntryPostings, new { query.EntryId }, cancellationToken: cancellationToken));
        return new EntryView(
            entry.EntryId,
            entry.Type,
            entry.TransactionId,
            entry.PlayerId,
            entry.RoundId,
            entry.ReversesEntryId,
            entry.PostedAt,
            [.. postings.Select(p => new EntryPostingView(p.PostingId, p.AccountId, p.AccountCode, p.AccountType, p.Currency, p.AmountMinor, p.BalanceAfterMinor))]);
    }
}

internal sealed class GetPendingWithdrawalsHandler(ReadConnectionFactory connections) : IQueryHandler<GetPendingWithdrawals, IReadOnlyList<PendingWithdrawalView>>
{
    public async Task<IReadOnlyList<PendingWithdrawalView>> HandleAsync(GetPendingWithdrawals query, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > GetPendingWithdrawals.MaxLimit)
        {
            throw new LedgerValidationException("limit", $"limit must be between 1 and {GetPendingWithdrawals.MaxLimit}.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<PendingWithdrawalRow>(new CommandDefinition(LedgerSql.PendingWithdrawals, new { Take = query.Limit }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new PendingWithdrawalView(r.WithdrawalId, r.PlayerId, r.Provider, r.Currency, r.AmountMinor, r.RequestedAt))];
    }
}

internal sealed class GetTrialBalanceHandler(ReadConnectionFactory connections) : IQueryHandler<GetTrialBalance, IReadOnlyList<TrialBalanceLine>>
{
    public async Task<IReadOnlyList<TrialBalanceLine>> HandleAsync(GetTrialBalance query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<TrialBalanceRow>(new CommandDefinition(LedgerSql.TrialBalance, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new TrialBalanceLine(r.Currency, r.AccountType, r.Accounts, r.BalanceMinor))];
    }
}

internal sealed class GetRoundSummaryHandler(ReadConnectionFactory connections) : IQueryHandler<GetRoundSummary, RoundSummaryView?>
{
    public async Task<RoundSummaryView?> HandleAsync(GetRoundSummary query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RoundSummaryRow>(new CommandDefinition(LedgerSql.RoundSummary, new { query.RoundId }, cancellationToken: cancellationToken));
        return row is null ? null : new RoundSummaryView(row.RoundId, row.PlayerId, row.Currency, row.BetMinor, row.WinMinor, row.RolledBackMinor, (int)row.Entries, row.UpdatedAt);
    }
}

// Dapper materialises these by reflection; the handlers map them to the public views.
#pragma warning disable CA1812
internal sealed class AccountRow
{
    public Guid AccountId { get; init; }

    public string Code { get; init; } = string.Empty;

    public AccountType Type { get; init; }

    public string? OwnerId { get; init; }

    public string Currency { get; init; } = string.Empty;

    public long BalanceMinor { get; init; }

    public long Version { get; init; }

    public DateTimeOffset OpenedAt { get; init; }

    public AccountView ToView() =>
        new(AccountId, Code, Type, OwnerId, Currency, BalanceMinor, !Type.IsPlayerWallet(), Version, OpenedAt);
}

internal sealed class StatementRow
{
    public long PostingId { get; init; }

    public Guid EntryId { get; init; }

    public EntryType EntryType { get; init; }

    public long AmountMinor { get; init; }

    public long? BalanceAfterMinor { get; init; }

    public DateTimeOffset PostedAt { get; init; }
}

internal sealed class EntryRow
{
    public Guid EntryId { get; init; }

    public EntryType Type { get; init; }

    public string TransactionId { get; init; } = string.Empty;

    public string PlayerId { get; init; } = string.Empty;

    public string? RoundId { get; init; }

    public Guid? ReversesEntryId { get; init; }

    public DateTimeOffset PostedAt { get; init; }
}

internal sealed class EntryPostingRow
{
    public long PostingId { get; init; }

    public Guid AccountId { get; init; }

    public string AccountCode { get; init; } = string.Empty;

    public AccountType AccountType { get; init; }

    public string Currency { get; init; } = string.Empty;

    public long AmountMinor { get; init; }

    public long? BalanceAfterMinor { get; init; }
}

internal sealed class PendingWithdrawalRow
{
    public string WithdrawalId { get; init; } = string.Empty;

    public string PlayerId { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public long AmountMinor { get; init; }

    public DateTimeOffset RequestedAt { get; init; }
}

internal sealed class TrialBalanceRow
{
    public string Currency { get; init; } = string.Empty;

    public AccountType AccountType { get; init; }

    public long Accounts { get; init; }

    public long BalanceMinor { get; init; }
}
internal sealed class RoundSummaryRow
{
    public string RoundId { get; init; } = string.Empty;

    public string PlayerId { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public long BetMinor { get; init; }

    public long WinMinor { get; init; }

    public long RolledBackMinor { get; init; }

    public long Entries { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

#pragma warning restore CA1812
