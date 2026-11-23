namespace Ledgerline.Domain.Accounts;

/// <summary>
/// The chart of accounts. Player wallets keep a stored balance that may never go negative;
/// operator accounts are unconstrained and their balance is derived from postings.
/// </summary>
public enum AccountType
{
    /// <summary>Withdrawable player money.</summary>
    PlayerCash,

    /// <summary>Promotional player money, not withdrawable until released into cash.</summary>
    PlayerBonus,

    /// <summary>The operator's gaming account: bets flow in, wins flow out. Its balance is gross gaming revenue.</summary>
    HouseGaming,

    /// <summary>Source of promotional money granted to players.</summary>
    BonusPool,

    /// <summary>Money moved through a payment provider (deposits in, completed payouts out).</summary>
    PspClearing,

    /// <summary>Payouts reserved from a player wallet that the provider has not confirmed yet.</summary>
    PspSuspense,
}

/// <summary>The two wallets a player has per currency.</summary>
public enum Wallet
{
    Cash,
    Bonus,
}

public static class AccountTypeExtensions
{
    /// <summary>Player wallets carry a stored balance, a version for optimistic concurrency and the no-overdraft rule.</summary>
    public static bool IsPlayerWallet(this AccountType type) => type is AccountType.PlayerCash or AccountType.PlayerBonus;

    public static AccountType ToAccountType(this Wallet wallet) => wallet switch
    {
        Wallet.Cash => AccountType.PlayerCash,
        Wallet.Bonus => AccountType.PlayerBonus,
        _ => throw new ArgumentOutOfRangeException(nameof(wallet), wallet, null),
    };

    public static Wallet? ToWallet(this AccountType type) => type switch
    {
        AccountType.PlayerCash => Wallet.Cash,
        AccountType.PlayerBonus => Wallet.Bonus,
        _ => null,
    };
}
