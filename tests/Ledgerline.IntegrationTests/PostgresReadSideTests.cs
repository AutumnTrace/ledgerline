using Ledgerline.IntegrationTests.Support;

namespace Ledgerline.IntegrationTests;

/// <summary>The Dapper read side and its type handlers against PostgreSQL's native uuid and timestamptz types.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresReadSideTests(ContainersFixture containers) : IAsyncLifetime
{
    private PostgresApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new PostgresApiFactory(await containers.CreateDatabaseAsync());
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Every_read_endpoint_works_on_PostgreSQL()
    {
        await _client.PostOkAsync("/v1/deposits", new { transactionId = "dep-1", provider = "northwind-pay", playerId = "p-1", currency = "EUR", amountMinor = 50_000 });
        for (var i = 1; i <= 7; i++)
        {
            await _client.PostOkAsync("/v1/wallet/bets", new { transactionId = $"bet-{i}", playerId = "p-1", roundId = "r-1", currency = "EUR", amountMinor = 1_000 });
        }

        var win = await _client.PostOkAsync("/v1/wallet/wins", new { transactionId = "win-1", playerId = "p-1", roundId = "r-1", currency = "EUR", amountMinor = 4_000 });
        await _client.PostOkAsync("/v1/withdrawals", new { withdrawalId = "wd-1", provider = "northwind-pay", playerId = "p-1", currency = "EUR", amountMinor = 2_000 });

        var wallets = await _client.GetOkAsync("/v1/players/p-1/wallets");
        var cash = wallets.GetProperty("wallets")[0];
        Assert.Equal(50_000 - 7_000 + 4_000 - 2_000, cash.GetProperty("balance").GetProperty("minor").GetInt64());
        var accountId = cash.GetProperty("accountId").GetGuid();

        // Keyset pages: 10 postings (deposit, 7 bets, win, withdrawal) in pages of 4.
        var first = await _client.GetOkAsync($"/v1/accounts/{accountId}/statement?limit=4");
        var second = await _client.GetOkAsync($"/v1/accounts/{accountId}/statement?limit=4&cursor={first.GetProperty("nextCursor").GetString()}");
        var third = await _client.GetOkAsync($"/v1/accounts/{accountId}/statement?limit=4&cursor={second.GetProperty("nextCursor").GetString()}");
        Assert.Equal([4, 4, 2], new[] { first, second, third }.Select(p => p.GetProperty("items").GetArrayLength()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, third.GetProperty("nextCursor").ValueKind);
        Assert.Equal("WithdrawalReserved", first.GetProperty("items")[0].GetProperty("entryType").GetString());
        Assert.Equal("DepositSettled", third.GetProperty("items")[1].GetProperty("entryType").GetString());

        var entry = await _client.GetOkAsync($"/v1/entries/{win.GetProperty("entryId").GetGuid()}");
        Assert.Equal(win.GetProperty("postedAt").GetDateTimeOffset(), entry.GetProperty("postedAt").GetDateTimeOffset());
        Assert.Equal(2, entry.GetProperty("postings").GetArrayLength());

        var house = await _client.GetOkAsync($"/v1/accounts/{win.GetProperty("postings")[0].GetProperty("accountId").GetGuid()}");
        Assert.Equal("derived", house.GetProperty("balanceSource").GetString());
        Assert.Equal(3_000, house.GetProperty("balance").GetProperty("minor").GetInt64());

        var pending = await _client.GetOkAsync("/v1/withdrawals/pending");
        Assert.Equal("wd-1", Assert.Single(pending.EnumerateArray()).GetProperty("withdrawalId").GetString());

        Assert.True((await _client.GetOkAsync("/v1/ledger/trial-balance")).GetProperty("balanced").GetBoolean());

        // The in-memory transport and the inbox projector, on PostgreSQL.
        var round = await _client.EventuallyAsync("/v1/rounds/r-1", r => r.GetProperty("entries").GetInt32() == 8);
        Assert.Equal(3_000, round.GetProperty("net").GetProperty("minor").GetInt64());
    }
}
