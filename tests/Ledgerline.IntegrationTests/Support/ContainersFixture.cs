using Ledgerline.IntegrationTests.Support;
using Npgsql;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(ContainersFixture))]

namespace Ledgerline.IntegrationTests.Support;

/// <summary>
/// One PostgreSQL and one Kafka container for the whole test run. Each test class gets its own
/// database inside the shared server, so classes stay isolated without paying for a container each.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:18-alpine")
        .WithCommand("-c", "max_connections=500")
        .Build();

    public KafkaContainer Kafka { get; } = new KafkaBuilder("apache/kafka:4.4.0").Build();

    public string KafkaBootstrapServers => Kafka.GetBootstrapAddress();

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(Postgres.StartAsync(), Kafka.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Kafka.DisposeAsync();
    }

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"ledger_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(Postgres.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString()) { Database = name, MaxPoolSize = 60 }.ConnectionString;
    }
}
