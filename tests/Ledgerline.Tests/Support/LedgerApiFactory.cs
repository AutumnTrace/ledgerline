using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledgerline.Tests.Support;

/// <summary>Hosts the real API in memory on its own SQLite file.</summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteTestDatabase _database = new();

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Ledger", _database.ConnectionString);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}

internal static class HttpExtensions
{
    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, LedgerApiFactory.Json, TestContext.Current.CancellationToken);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(LedgerApiFactory.Json, TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException("Empty response body.");

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        await response.ReadAsync<JsonElement>();
}
