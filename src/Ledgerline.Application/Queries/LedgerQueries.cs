using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Application.Queries;

// Read-side contracts. The handlers live in the infrastructure layer and run hand-written SQL
// through Dapper; they never load the EF Core aggregates.

public sealed record GetPlayerWallets(string PlayerId);

public sealed record GetAccount(Guid AccountId);

/// <summary>One page of an account statement, newest first. <paramref name="Cursor"/> comes from the previous page.</summary>
public sealed record GetStatement(Guid AccountId, int Limit, string? Cursor)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record GetEntry(Guid EntryId);

public sealed record GetPendingWithdrawals(int Limit)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
}

public sealed record GetTrialBalance;

/// <param name="BalanceIsDerived">
/// False for player wallets (stored balance, kept in step by every posting). True for operator accounts,
/// whose balance is computed from their postings at read time.
/// </param>
public sealed record AccountView(
    Guid AccountId,
    string Code,
    AccountType Type,
    string? OwnerId,
    string Currency,
    long BalanceMinor,
    bool BalanceIsDerived,
    long Version,
    DateTimeOffset OpenedAt);

public sealed record StatementPage(AccountView Account, IReadOnlyList<StatementLine> Lines, string? NextCursor);

public sealed record StatementLine(long PostingId, Guid EntryId, EntryType EntryType, long AmountMinor, long? BalanceAfterMinor, DateTimeOffset PostedAt);

public sealed record EntryView(
    Guid EntryId,
    EntryType Type,
    string TransactionId,
    string PlayerId,
    string? RoundId,
    Guid? ReversesEntryId,
    DateTimeOffset PostedAt,
    IReadOnlyList<EntryPostingView> Postings);

public sealed record EntryPostingView(
    long PostingId,
    Guid AccountId,
    string AccountCode,
    AccountType AccountType,
    string Currency,
    long AmountMinor,
    long? BalanceAfterMinor);

public sealed record PendingWithdrawalView(
    string WithdrawalId,
    string PlayerId,
    string Provider,
    string Currency,
    long AmountMinor,
    DateTimeOffset RequestedAt);

/// <summary>Net balance per account type and currency. Across all types, each currency must net to zero.</summary>
public sealed record TrialBalanceLine(string Currency, AccountType AccountType, long Accounts, long BalanceMinor);

public sealed record GetRoundSummary(string RoundId);

/// <summary>Per-round totals from the asynchronous projection. Eventually consistent with the journal.</summary>
public sealed record RoundSummaryView(
    string RoundId,
    string PlayerId,
    string Currency,
    long BetMinor,
    long WinMinor,
    long RolledBackMinor,
    int Entries,
    DateTimeOffset UpdatedAt)
{
    /// <summary>What the house kept on the round: bets, minus rolled-back bets, minus wins.</summary>
    public long NetMinor => BetMinor - RolledBackMinor - WinMinor;
}
