using System.Text.Json;
using Ledgerline.Application.Commands;
using Ledgerline.Domain;
using Ledgerline.Infrastructure.Messaging;
using Ledgerline.Infrastructure.Projections;
using Ledgerline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.Tests.Infrastructure;

/// <summary>The inbox-deduplicated consumer that builds per-round totals from the ledger stream.</summary>
public sealed class RoundProjectionTests : IAsyncLifetime
{
    private LedgerServices _ledger = null!;

    public async ValueTask InitializeAsync() => _ledger = await LedgerServices.CreateAsync();

    public ValueTask DisposeAsync() => _ledger.DisposeAsync();

    [Fact]
    public async Task A_redelivered_message_is_applied_once()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
        await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-1"));
        var bet = (await OutboxPayloadsAsync())[^1];

        Assert.True(await ProjectAsync(bet));
        Assert.False(await ProjectAsync(bet));
        Assert.False(await ProjectAsync(bet));

        var round = await RoundAsync("round-1");
        Assert.Equal(2_500, round.BetMinor);
        Assert.Equal(1, round.Entries);
        Assert.Equal(1, await _ledger.QueryAsync(db => db.InboxMessages.CountAsync()));
    }

    [Fact]
    public async Task Bets_wins_and_rollbacks_add_up_per_round()
    {
        await _ledger.SendAsync(new SettleDeposit("dep-1", "northwind-pay", "p-1", Eur(10_000)));
        await _ledger.SendAsync(new PlaceBet("bet-1", "p-1", Eur(2_500), "round-7"));
        await _ledger.SendAsync(new PlaceBet("bet-2", "p-1", Eur(1_000), "round-7"));
        await _ledger.SendAsync(new CreditWin("win-1", "p-1", Eur(6_000), "round-7"));
        await _ledger.SendAsync(new RollbackBet("rb-1", "p-1", "bet-2"));

        foreach (var message in await OutboxPayloadsAsync())
        {
            await ProjectAsync(message);
        }

        var round = await RoundAsync("round-7");
        Assert.Equal(3_500, round.BetMinor);
        Assert.Equal(6_000, round.WinMinor);
        Assert.Equal(1_000, round.RolledBackMinor);
        Assert.Equal(4, round.Entries);
        Assert.Equal("EUR", round.Currency);

        // Deposits carry no round and touch no projection, but are still recorded as processed.
        Assert.Equal(5, await _ledger.QueryAsync(db => db.InboxMessages.CountAsync()));
    }

    private async Task<bool> ProjectAsync((Guid Id, string Payload) message)
    {
        await using var scope = _ledger.Provider.CreateAsyncScope();
        var projector = scope.ServiceProvider.GetRequiredService<RoundSummaryProjector>();
        var contract = JsonSerializer.Deserialize(message.Payload, LedgerJsonContext.Default.LedgerEntryPostedV1)!;
        return await projector.ProjectAsync(message.Id, contract, TestContext.Current.CancellationToken);
    }

    private Task<List<(Guid Id, string Payload)>> OutboxPayloadsAsync() =>
        _ledger.QueryAsync(async db => (await db.OutboxMessages.OrderBy(m => m.Id).Select(m => new { m.MessageId, m.Payload }).ToListAsync())
            .Select(m => (m.MessageId, m.Payload))
            .ToList());

    private Task<RoundSummary> RoundAsync(string roundId) =>
        _ledger.QueryAsync(db => db.RoundSummaries.SingleAsync(r => r.RoundId == roundId));

    private static Money Eur(long minor) => new(minor, Currency.EUR);
}
