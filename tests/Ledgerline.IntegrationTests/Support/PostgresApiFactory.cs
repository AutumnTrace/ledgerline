using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledgerline.IntegrationTests.Support;

/// <summary>The real API on PostgreSQL, optionally publishing to the Kafka container.</summary>
internal sealed class PostgresApiFactory(string connectionString, string? kafkaBootstrap = null, string? topic = null) : WebApplicationFactory<Program>
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Postgres");
        builder.UseSetting("ConnectionStrings:Ledger", connectionString);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Outbox:PollingInterval", "00:00:00.050");
        if (kafkaBootstrap is not null)
        {
            builder.UseSetting("Messaging:Transport", "Kafka");
            builder.UseSetting("Messaging:Kafka:BootstrapServers", kafkaBootstrap);
            builder.UseSetting("Messaging:Kafka:ConsumerGroup", $"ledgerline-it-{Guid.NewGuid():N}");
        }

        if (topic is not null)
        {
            builder.UseSetting("Messaging:Topic", topic);
        }
    }
}

internal static class HttpExtensions
{
    public static async Task<JsonElement> PostOkAsync(this HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, PostgresApiFactory.Json, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"POST {url}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> GetOkAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"GET {url}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Polls until <paramref name="ready"/> holds, for reads that are eventually consistent.</summary>
    public static async Task<JsonElement> EventuallyAsync(this HttpClient client, string url, Func<JsonElement, bool> ready, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        JsonElement last = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
            if (response.IsSuccessStatusCode)
            {
                last = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
                if (ready(last))
                {
                    return last;
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"{url} did not reach the expected state in time. Last: {last}");
        return last;
    }
}
