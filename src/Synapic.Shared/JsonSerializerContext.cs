using System.Text.Json;
using System.Text.Json.Serialization;
using Synapic.Shared.Contracts;

namespace Synapic.Shared;

/// <summary>
/// Source-generated JSON serializer context for the C# ↔ Python HTTP contract.
/// camelCase via explicit JsonPropertyName attributes on every property, so the
/// wire format is stable regardless of hosting runtime JSON defaults.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ModelDownloadProgress))]
[JsonSerializable(typeof(ModelInfo))]
[JsonSerializable(typeof(ModelInfo[]))]
[JsonSerializable(typeof(DownloadRequest))]
[JsonSerializable(typeof(TagRequest))]
[JsonSerializable(typeof(TagResponse))]
[JsonSerializable(typeof(TagOptions))]
[JsonSerializable(typeof(ScoringResult))]
[JsonSerializable(typeof(ScoredKeyword))]
[JsonSerializable(typeof(ConfigDto))]
[JsonSerializable(typeof(PromptDefaultsDto))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(JsonElement))]
public partial class SynapicJsonContext : JsonSerializerContext;
