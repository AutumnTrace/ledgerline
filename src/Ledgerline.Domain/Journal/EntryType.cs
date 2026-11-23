namespace Ledgerline.Domain.Journal;

public enum EntryType
{
    /// <summary>A PSP confirmed a deposit: PSP clearing to player cash.</summary>
    DepositSettled,

    /// <summary>Seamless wallet bet: player cash to house.</summary>
    BetDebit,

    /// <summary>Seamless wallet win: house to player cash.</summary>
    WinCredit,

    /// <summary>Cancels an earlier bet by posting its exact opposite.</summary>
    BetRollback,

    /// <summary>Withdrawal requested: player cash moves into PSP suspense (the hold).</summary>
    WithdrawalReserved,

    /// <summary>PSP confirmed the payout: suspense to PSP clearing.</summary>
    WithdrawalCompleted,

    /// <summary>Payout failed or was cancelled: suspense back to player cash.</summary>
    WithdrawalReversed,

    /// <summary>Bonus pool to player bonus wallet.</summary>
    BonusGranted,

    /// <summary>Between two wallets of the same player, e.g. releasing bonus money into cash.</summary>
    WalletTransfer,
}
