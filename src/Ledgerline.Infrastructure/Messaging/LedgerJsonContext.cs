using System.Text.Json;
using System.Text.Json.Serialization;
using Ledgerline.Application.IntegrationEvents;

namespace Ledgerline.Infrastructure.Messaging;

/// <summary>Source-generated serializer for the wire contracts: no reflection on the hot path, stable camelCase JSON.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(LedgerEntryPostedV1))]
public sealed partial class LedgerJsonContext : JsonSerializerContext;
