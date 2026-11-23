using System.Net;
using System.Text.Json;
using Ledgerline.Tests.Support;

namespace Ledgerline.Tests.Api;

/// <summary>The Dapper read side, through HTTP, on SQLite.</summary>
public sealed class QueryApiTests(LedgerApiFactory factory) : IClassFixture<LedgerApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Statements_page_newest_first_with_a_keyset_cursor()
    {
        var player = NewPlayer();
        await Post("/v1/deposits", new { transactionId = $"{player}-dep", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 100_000 });
        for (var i = 1; i <= 24; i++)
        {
            await Post("/v1/wallet/bets", new { transactionId = $"{player}-bet-{i}", playerId = player, roundId = $"r-{i}", currency = "EUR", amountMinor = i * 100 });
        }

        var accountId = await CashAccountId(player);
        var pages = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var url = $"/v1/accounts/{accountId}/statement?limit=10" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var page = await Get(url);
            pages.Add(page);
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal([10, 10, 5], pages.Select(p => p.GetProperty("items").GetArrayLength()));
        var items = pages.SelectMany(p => p.GetProperty("items").EnumerateArray()).ToList();
        var ids = items.Select(i => i.GetProperty("postingId").GetInt64()).ToList();
        Assert.Equal(ids.OrderDescending(), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Newest first: the last bet (24.00) on top, the deposit at the very bottom.
        Assert.Equal(-2_400, items[0].GetProperty("amount").GetProperty("minor").GetInt64());
        Assert.Equal("DepositSettled", items[^1].GetProperty("entryType").GetString());
        Assert.Equal(100_000 - 30_000, items[0].GetProperty("balanceAfter").GetProperty("minor").GetInt64());
        Assert.Equal(70_000, pages[0].GetProperty("account").GetProperty("balance").GetProperty("minor").GetInt64());
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=AQAAAAAAAAAA")]
    public async Task Bad_paging_parameters_are_a_validation_problem(string query)
    {
        var player = NewPlayer();
        await Post("/v1/deposits", new { transactionId = $"{player}-dep", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 100 });

        var response = await _client.GetAsync($"/v1/accounts/{await CashAccountId(player)}/statement?{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_accounts_and_entries_are_404()
    {
        var account = await _client.GetAsync($"/v1/accounts/{Guid.NewGuid()}/statement", TestContext.Current.CancellationToken);
        var entry = await _client.GetAsync($"/v1/entries/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, account.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, entry.StatusCode);
        Assert.Equal("not-found", (await entry.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_player_sees_every_wallet_with_its_stored_balance()
    {
        var player = NewPlayer();
        await Post("/v1/deposits", new { transactionId = $"{player}-eur", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 2_500 });
        await Post("/v1/deposits", new { transactionId = $"{player}-jpy", provider = "northwind-pay", playerId = player, currency = "JPY", amountMinor = 3_000 });
        await Post("/v1/bonuses", new { transactionId = $"{player}-bonus", playerId = player, currency = "EUR", amountMinor = 500 });

        var wallets = (await Get($"/v1/players/{player}/wallets")).GetProperty("wallets").EnumerateArray()
            .Select(w => (Code: w.GetProperty("code").GetString(), Amount: w.GetProperty("balance").GetProperty("amount").GetString(), Source: w.GetProperty("balanceSource").GetString()))
            .ToList();

        Assert.Equal(
            [($"player:{player}:cash:EUR", "25.00", "stored"), ($"player:{player}:bonus:EUR", "5.00", "stored"), ($"player:{player}:cash:JPY", "3000", "stored")],
            wallets);
    }

    [Fact]
    public async Task An_entry_shows_all_postings_with_account_codes()
    {
        var player = NewPlayer();
        await Post("/v1/deposits", new { transactionId = $"{player}-dep", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 5_000 });
        var bet = await Post("/v1/wallet/bets", new { transactionId = $"{player}-bet", playerId = player, roundId = "r-1", currency = "EUR", amountMinor = 1_200 });
        var entryId = bet.GetProperty("entryId").GetGuid();

        var entry = await Get($"/v1/entries/{entryId}");

        Assert.Equal("BetDebit", entry.GetProperty("type").GetString());
        Assert.Equal("r-1", entry.GetProperty("roundId").GetString());
        var postings = entry.GetProperty("postings").EnumerateArray().ToList();
        Assert.Equal([$"player:{player}:cash:EUR", "house:gaming:EUR"], postings.Select(p => p.GetProperty("accountCode").GetString()));
        Assert.Equal(0, postings.Sum(p => p.GetProperty("amount").GetProperty("minor").GetInt64()));
    }

    [Fact]
    public async Task Operator_accounts_report_a_derived_balance()
    {
        var player = NewPlayer();
        await Post("/v1/deposits", new { transactionId = $"{player}-dep", provider = "eastbank-pay", playerId = player, currency = "CHF", amountMinor = 7_000 });
        var deposit = await Post("/v1/deposits", new { transactionId = $"{player}-dep-2", provider = "eastbank-pay", playerId = player, currency = "CHF", amountMinor = 3_000 });
        var clearingId = deposit.GetProperty("postings")[0].GetProperty("accountId").GetGuid();

        var clearing = await Get($"/v1/accounts/{clearingId}");

        Assert.Equal("psp:eastbank-pay:clearing:CHF", clearing.GetProperty("code").GetString());
        Assert.Equal("derived", clearing.GetProperty("balanceSource").GetString());
        Assert.Equal(-10_000, clearing.GetProperty("balance").GetProperty("minor").GetInt64());
    }

    [Fact]
    public async Task Pending_withdrawals_list_only_open_holds()
    {
        var player = NewPlayer();
        await Post("/v1/deposits", new { transactionId = $"{player}-dep", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 10_000 });
        await Post("/v1/withdrawals", new { withdrawalId = $"{player}-wd-1", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 1_000 });
        await Post("/v1/withdrawals", new { withdrawalId = $"{player}-wd-2", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 2_000 });
        await Post($"/v1/withdrawals/{player}-wd-1/complete", new { });

        var pending = (await Get("/v1/withdrawals/pending?limit=500")).EnumerateArray()
            .Where(w => w.GetProperty("playerId").GetString() == player)
            .Select(w => w.GetProperty("withdrawalId").GetString())
            .ToList();

        Assert.Equal([$"{player}-wd-2"], pending);
    }

    [Fact]
    public async Task The_trial_balance_nets_to_zero_in_every_currency()
    {
        var player = NewPlayer();
        await Post("/v1/deposits", new { transactionId = $"{player}-dep", provider = "northwind-pay", playerId = player, currency = "GBP", amountMinor = 9_000 });
        await Post("/v1/wallet/bets", new { transactionId = $"{player}-bet", playerId = player, roundId = "r-1", currency = "GBP", amountMinor = 4_000 });
        await Post("/v1/wallet/wins", new { transactionId = $"{player}-win", playerId = player, roundId = "r-1", currency = "GBP", amountMinor = 1_500 });

        var trial = await Get("/v1/ledger/trial-balance");

        Assert.True(trial.GetProperty("balanced").GetBoolean());
        var gbp = trial.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("currency").GetString() == "GBP");
        Assert.Equal(0, gbp.GetProperty("net").GetProperty("minor").GetInt64());
        Assert.Contains(gbp.GetProperty("lines").EnumerateArray(), l => l.GetProperty("accountType").GetString() == "HouseGaming");
    }

    private async Task<JsonElement> Post(string url, object body)
    {
        var response = await _client.PostJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return await response.ReadJsonAsync();
    }

    private async Task<JsonElement> Get(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private async Task<Guid> CashAccountId(string player) =>
        (await Get($"/v1/players/{player}/wallets")).GetProperty("wallets")[0].GetProperty("accountId").GetGuid();

    private static string NewPlayer() => $"p-{Guid.NewGuid():N}"[..20];
}
