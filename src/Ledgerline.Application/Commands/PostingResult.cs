using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Application;

/// <summary>
/// What a command produced. A replay returns the same entry with the balances as they were right
/// after the original posting, not the current ones, so retries are indistinguishable from the first call.
/// </summary>
public sealed record PostingResult(
    Guid EntryId,
    EntryType Type,
    string TransactionId,
    string PlayerId,
    DateTimeOffset PostedAt,
    bool Replayed,
    IReadOnlyList<PostingLine> Postings)
{
    /// <summary>Player wallets touched by the entry, with their balance after it.</summary>
    public IEnumerable<PostingLine> Wallets => Postings.Where(p => p.BalanceAfter is not null);

    public static PostingResult From(JournalEntry entry, IReadOnlyDictionary<Guid, Account> accounts, bool replayed)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(accounts);
        var lines = entry.Postings
            .Select(p =>
            {
                var account = accounts[p.AccountId];
                return new PostingLine(account.Id, account.Code, account.Type, p.Amount, p.BalanceAfter);
            })
            .ToList();

        return new PostingResult(entry.Id, entry.Type, entry.TransactionId, entry.PlayerId, entry.PostedAt, replayed, lines);
    }
}

public sealed record PostingLine(Guid AccountId, string AccountCode, AccountType AccountType, Money Amount, Money? BalanceAfter);
