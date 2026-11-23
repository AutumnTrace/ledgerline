using Ledgerline.Application.Queries;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Api.Contracts;

/// <param name="BalanceSource"><c>stored</c> for player wallets, <c>derived</c> (sum of postings) for operator accounts.</param>
public sealed record AccountResponse(
    Guid AccountId,
    string Code,
    AccountType Type,
    string? OwnerId,
    MoneyResponse Balance,
    string BalanceSource,
    long Version,
    DateTimeOffset OpenedAt)
{
    public static AccountResponse From(AccountView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new AccountResponse(
            view.AccountId,
            view.Code,
            view.Type,
            view.OwnerId,
            MoneyResponse.From(view.BalanceMinor, view.Currency),
            view.BalanceIsDerived ? "derived" : "stored",
            view.Version,
            view.OpenedAt);
    }
}

public sealed record PlayerWalletsResponse(string PlayerId, IReadOnlyList<AccountResponse> Wallets);

public sealed record StatementResponse(AccountResponse Account, IReadOnlyList<StatementLineResponse> Items, string? NextCursor)
{
    public static StatementResponse From(StatementPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var currency = page.Account.Currency;
        return new StatementResponse(
            AccountResponse.From(page.Account),
            [.. page.Lines.Select(l => new StatementLineResponse(
                l.PostingId,
                l.EntryId,
                l.EntryType,
                MoneyResponse.From(l.AmountMinor, currency),
                l.BalanceAfterMinor is { } after ? MoneyResponse.From(after, currency) : null,
                l.PostedAt))],
            page.NextCursor);
    }
}

public sealed record StatementLineResponse(long PostingId, Guid EntryId, EntryType EntryType, MoneyResponse Amount, MoneyResponse? BalanceAfter, DateTimeOffset PostedAt);

public sealed record EntryResponse(
    Guid EntryId,
    EntryType Type,
    string TransactionId,
    string PlayerId,
    string? RoundId,
    Guid? ReversesEntryId,
    DateTimeOffset PostedAt,
    IReadOnlyList<EntryPostingResponse> Postings)
{
    public static EntryResponse From(EntryView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new EntryResponse(
            view.EntryId,
            view.Type,
            view.TransactionId,
            view.PlayerId,
            view.RoundId,
            view.ReversesEntryId,
            view.PostedAt,
            [.. view.Postings.Select(p => new EntryPostingResponse(
                p.PostingId,
                p.AccountId,
                p.AccountCode,
                p.AccountType,
                MoneyResponse.From(p.AmountMinor, p.Currency),
                p.BalanceAfterMinor is { } after ? MoneyResponse.From(after, p.Currency) : null))]);
    }
}

public sealed record EntryPostingResponse(long PostingId, Guid AccountId, string AccountCode, AccountType AccountType, MoneyResponse Amount, MoneyResponse? BalanceAfter);

public sealed record PendingWithdrawalResponse(string WithdrawalId, string PlayerId, string Provider, MoneyResponse Amount, DateTimeOffset RequestedAt);

/// <summary>The classic accounting check: in every currency, all account balances net to zero.</summary>
public sealed record TrialBalanceResponse(bool Balanced, IReadOnlyList<CurrencyTrialBalance> Currencies)
{
    public static TrialBalanceResponse From(IReadOnlyList<TrialBalanceLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var currencies = lines
            .GroupBy(l => l.Currency)
            .Select(g =>
            {
                var net = g.Sum(l => l.BalanceMinor);
                return new CurrencyTrialBalance(
                    g.Key,
                    MoneyResponse.From(net, g.Key),
                    net == 0,
                    [.. g.Select(l => new TrialBalanceLineResponse(l.AccountType, l.Accounts, MoneyResponse.From(l.BalanceMinor, g.Key)))]);
            })
            .ToList();

        return new TrialBalanceResponse(currencies.TrueForAll(c => c.Balanced), currencies);
    }
}

public sealed record CurrencyTrialBalance(string Currency, MoneyResponse Net, bool Balanced, IReadOnlyList<TrialBalanceLineResponse> Lines);

public sealed record TrialBalanceLineResponse(AccountType AccountType, long Accounts, MoneyResponse Balance);
