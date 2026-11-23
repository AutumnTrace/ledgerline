namespace Ledgerline.Domain.Accounts;

/// <summary>
/// The natural key of an account: its type, owner (player or provider) and currency.
/// <see cref="Code"/> renders it as a stable, human-readable string such as <c>player:p-1001:cash:EUR</c>.
/// </summary>
public sealed record AccountKey
{
    private AccountKey(AccountType type, string? owner, Currency currency)
    {
        Type = type;
        Owner = owner;
        Currency = currency;
    }

    public AccountType Type { get; }

    /// <summary>Player id for wallets, provider code for PSP accounts, null for house accounts.</summary>
    public string? Owner { get; }

    public Currency Currency { get; }

    public string Code => Type switch
    {
        AccountType.PlayerCash => $"player:{Owner}:cash:{Currency.Code}",
        AccountType.PlayerBonus => $"player:{Owner}:bonus:{Currency.Code}",
        AccountType.HouseGaming => $"house:gaming:{Currency.Code}",
        AccountType.BonusPool => $"house:bonus-pool:{Currency.Code}",
        AccountType.PspClearing => $"psp:{Owner}:clearing:{Currency.Code}",
        AccountType.PspSuspense => $"psp:{Owner}:suspense:{Currency.Code}",
        _ => throw new InvalidOperationException($"Unknown account type {Type}."),
    };

    public static AccountKey PlayerWallet(string playerId, Wallet wallet, Currency currency) =>
        new(wallet.ToAccountType(), ExternalId.Require(playerId, "playerId"), currency);

    public static AccountKey PlayerCash(string playerId, Currency currency) => PlayerWallet(playerId, Wallet.Cash, currency);

    public static AccountKey PlayerBonus(string playerId, Currency currency) => PlayerWallet(playerId, Wallet.Bonus, currency);

    public static AccountKey HouseGaming(Currency currency) => new(AccountType.HouseGaming, null, currency);

    public static AccountKey BonusPool(Currency currency) => new(AccountType.BonusPool, null, currency);

    public static AccountKey PspClearing(string provider, Currency currency) =>
        new(AccountType.PspClearing, ExternalId.RequireProvider(provider), currency);

    public static AccountKey PspSuspense(string provider, Currency currency) =>
        new(AccountType.PspSuspense, ExternalId.RequireProvider(provider), currency);

    public override string ToString() => Code;
}
