using System.Net;
using System.Text;
using System.Text.Json;
using Ledgerline.Tests.Support;

namespace Ledgerline.Tests.Api;

/// <summary>The HTTP contract: status codes, replay semantics and RFC 9457 problem details.</summary>
public sealed class CommandApiTests(LedgerApiFactory factory) : IClassFixture<LedgerApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task First_call_is_201_and_a_replay_is_200_with_the_same_body()
    {
        var player = NewPlayer();
        await Deposit(player, 10_000);
        var bet = new { transactionId = $"{player}-bet-1", playerId = player, roundId = "round-1", currency = "EUR", amountMinor = 2_500 };

        var first = await _client.PostJsonAsync("/v1/wallet/bets", bet);
        var replay = await _client.PostJsonAsync("/v1/wallet/bets", bet);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        var created = await first.ReadJsonAsync();
        Assert.Equal($"/v1/entries/{created.GetProperty("entryId").GetGuid()}", first.Headers.Location?.OriginalString);
        Assert.Equal(7_500, created.GetProperty("balances")[0].GetProperty("balance").GetProperty("minor").GetInt64());
        Assert.Equal("75.00", created.GetProperty("balances")[0].GetProperty("balance").GetProperty("amount").GetString());

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotent-Replayed")));
        var replayed = await replay.ReadJsonAsync();
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal(created.GetProperty("entryId").GetGuid(), replayed.GetProperty("entryId").GetGuid());
        Assert.Equal(created.GetProperty("postings").GetRawText(), replayed.GetProperty("postings").GetRawText());
    }

    [Fact]
    public async Task Invalid_requests_get_a_validation_problem_listing_each_field()
    {
        var response = await _client.PostJsonAsync("/v1/wallet/bets", new { transactionId = "has space", playerId = "p-1", currency = "EUR", amountMinor = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var errors = (await response.ReadJsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("TransactionId", out _));
        Assert.True(errors.TryGetProperty("RoundId", out _));
        Assert.True(errors.TryGetProperty("AmountMinor", out _));
    }

    [Fact]
    public async Task Unknown_currencies_are_a_validation_problem()
    {
        var response = await _client.PostJsonAsync("/v1/deposits", new { transactionId = "d-1", provider = "northwind-pay", playerId = "p-1", currency = "XXX", amountMinor = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("validation-failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("currency", out _));
    }

    [Fact]
    public async Task Insufficient_funds_is_422_with_the_available_balance()
    {
        var player = NewPlayer();
        await Deposit(player, 1_000);

        var response = await _client.PostJsonAsync("/v1/wallet/bets", new { transactionId = $"{player}-bet", playerId = player, roundId = "r-1", currency = "EUR", amountMinor = 1_001 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("insufficient-funds", problem.GetProperty("code").GetString());
        Assert.Equal("https://github.com/AutumnTrace/ledgerline/blob/main/docs/errors.md#insufficient-funds", problem.GetProperty("type").GetString());
        Assert.Equal(1_000, problem.GetProperty("availableMinor").GetInt64());
        Assert.Equal(1_001, problem.GetProperty("requestedMinor").GetInt64());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Reusing_a_transaction_id_with_a_different_body_is_409()
    {
        var player = NewPlayer();
        await Deposit(player, 10_000);
        await _client.PostJsonAsync("/v1/wallet/bets", new { transactionId = $"{player}-tx", playerId = player, roundId = "r-1", currency = "EUR", amountMinor = 100 });

        var response = await _client.PostJsonAsync("/v1/wallet/wins", new { transactionId = $"{player}-tx", playerId = player, roundId = "r-1", currency = "EUR", amountMinor = 100 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency-key-reused", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Malformed_json_is_400_not_500()
    {
        using var content = new StringContent("{\"transactionId\":", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/v1/wallet/bets", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_withdrawal_goes_through_reserve_and_complete_and_rejects_a_late_reversal()
    {
        var player = NewPlayer();
        await Deposit(player, 10_000);
        var withdrawalId = $"{player}-wd";

        var reserve = await _client.PostJsonAsync("/v1/withdrawals", new { withdrawalId, provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor = 4_000 });
        var complete = await _client.PostJsonAsync($"/v1/withdrawals/{withdrawalId}/complete", new { });
        var completeAgain = await _client.PostJsonAsync($"/v1/withdrawals/{withdrawalId}/complete", new { });
        var reverse = await _client.PostJsonAsync($"/v1/withdrawals/{withdrawalId}/reverse", new { reason = "Provider timeout" });

        Assert.Equal(HttpStatusCode.Created, reserve.StatusCode);
        Assert.Equal("WithdrawalReserved", (await reserve.ReadJsonAsync()).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Created, complete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completeAgain.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reverse.StatusCode);
        Assert.Equal("invalid-state", (await reverse.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Completing_an_unknown_withdrawal_is_404()
    {
        var response = await _client.PostJsonAsync("/v1/withdrawals/does-not-exist/complete", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not-found", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Transfers_take_wallet_names_as_strings()
    {
        var player = NewPlayer();
        await _client.PostJsonAsync("/v1/bonuses", new { transactionId = $"{player}-bonus", playerId = player, currency = "EUR", amountMinor = 2_000 });

        var response = await _client.PostJsonAsync("/v1/transfers", new { transactionId = $"{player}-tr", playerId = player, from = "Bonus", to = "Cash", currency = "EUR", amountMinor = 500 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var balances = (await response.ReadJsonAsync()).GetProperty("balances");
        Assert.Equal(["Bonus", "Cash"], balances.EnumerateArray().Select(b => b.GetProperty("wallet").GetString()));
    }

    [Fact]
    public async Task Health_probes_and_the_OpenAPI_document_are_served()
    {
        var live = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var ready = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var openApi = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        var paths = (await openApi.ReadJsonAsync()).GetProperty("paths");
        Assert.True(paths.TryGetProperty("/v1/wallet/bets", out _));
        Assert.True(paths.TryGetProperty("/v1/withdrawals/{withdrawalId}/complete", out _));
    }

    private async Task Deposit(string player, long amountMinor)
    {
        var response = await _client.PostJsonAsync("/v1/deposits", new { transactionId = $"{player}-dep-{amountMinor}", provider = "northwind-pay", playerId = player, currency = "EUR", amountMinor });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static string NewPlayer() => $"p-{Guid.NewGuid():N}"[..20];
}
