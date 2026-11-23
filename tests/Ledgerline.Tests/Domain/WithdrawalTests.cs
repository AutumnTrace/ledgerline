using Ledgerline.Domain;
using Ledgerline.Domain.Withdrawals;

namespace Ledgerline.Tests.Domain;

public sealed class WithdrawalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_withdrawal_is_reserved()
    {
        var withdrawal = Reserve();

        Assert.Equal(WithdrawalStatus.Reserved, withdrawal.Status);
        Assert.Equal(new Money(5000, Currency.EUR), withdrawal.Amount);
        Assert.Null(withdrawal.ResolvedAt);
    }

    [Fact]
    public void Completing_resolves_it_once()
    {
        var withdrawal = Reserve();
        var entryId = Guid.CreateVersion7();

        withdrawal.Complete(entryId, Now.AddMinutes(5));

        Assert.Equal(WithdrawalStatus.Completed, withdrawal.Status);
        Assert.Equal(entryId, withdrawal.ResolutionEntryId);
        Assert.Equal(Now.AddMinutes(5), withdrawal.ResolvedAt);
        Assert.Equal(1, withdrawal.Version);
        Assert.Throws<InvalidLedgerStateException>(() => withdrawal.Complete(Guid.CreateVersion7(), Now));
        Assert.Throws<InvalidLedgerStateException>(() => withdrawal.Reverse(Guid.CreateVersion7(), "late failure", Now));
    }

    [Fact]
    public void Reversing_records_the_reason()
    {
        var withdrawal = Reserve();

        withdrawal.Reverse(Guid.CreateVersion7(), "Provider declined the payout", Now);

        Assert.Equal(WithdrawalStatus.Reversed, withdrawal.Status);
        Assert.Equal("Provider declined the payout", withdrawal.ReversalReason);
        Assert.Throws<InvalidLedgerStateException>(() => withdrawal.Complete(Guid.CreateVersion7(), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reversing_needs_a_reason(string reason)
    {
        var withdrawal = Reserve();

        Assert.Throws<LedgerValidationException>(() => withdrawal.Reverse(Guid.CreateVersion7(), reason, Now));
        Assert.Equal(WithdrawalStatus.Reserved, withdrawal.Status);
    }

    [Fact]
    public void Only_positive_amounts_can_be_withdrawn()
    {
        Assert.Throws<LedgerValidationException>(
            () => Withdrawal.Reserve("wd-1", "p-1", "northwind", new Money(0, Currency.EUR), Guid.CreateVersion7(), Now));
    }

    private static Withdrawal Reserve() =>
        Withdrawal.Reserve("wd-1", "p-1", "northwind", new Money(5000, Currency.EUR), Guid.CreateVersion7(), Now);
}
