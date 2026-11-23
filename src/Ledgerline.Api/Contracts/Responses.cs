using Ledgerline.Application;
using Ledgerline.Domain;
using Ledgerline.Domain.Accounts;
using Ledgerline.Domain.Journal;

namespace Ledgerline.Api.Contracts;

/// <summary>Money on the wire: exact minor units plus a formatted major-unit string for humans.</summary>
public sealed record MoneyResponse(long Minor, string Currency, string Amount)
{
    public static MoneyResponse From(Money money) => new(money.Minor, money.Currency.Code, money.FormatAmount());

    public static MoneyResponse From(long minor, string currency) => From(new Money(minor, Domain.Currency.Of(currency)));
}

public sealed record PostingResponse(
    Guid EntryId,
    EntryType Type,
    string TransactionId,
    string PlayerId,
    DateTimeOffset PostedAt,
    bool Replayed,
    IReadOnlyList<WalletBalanceResponse> Balances,
    IReadOnlyList<PostingLineResponse> Postings)
{
    public static PostingResponse From(PostingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new PostingResponse(
            result.EntryId,
            result.Type,
            result.TransactionId,
            result.PlayerId,
            result.PostedAt,
            result.Replayed,
            [.. result.Wallets.Select(w => new WalletBalanceResponse(w.AccountId, w.AccountCode, w.AccountType.ToWallet()!.Value, MoneyResponse.From(w.BalanceAfter!.Value)))],
            [.. result.Postings.Select(p => new PostingLineResponse(
                p.AccountId,
                p.AccountCode,
                p.AccountType,
                MoneyResponse.From(p.Amount),
                p.BalanceAfter is { } after ? MoneyResponse.From(after) : null))]);
    }
}

/// <summary>A player wallet's balance right after the entry (on a replay: after the original entry).</summary>
public sealed record WalletBalanceResponse(Guid AccountId, string AccountCode, Wallet Wallet, MoneyResponse Balance);

public sealed record PostingLineResponse(Guid AccountId, string AccountCode, AccountType AccountType, MoneyResponse Amount, MoneyResponse? BalanceAfter);
