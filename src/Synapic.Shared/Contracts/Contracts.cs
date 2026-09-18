using System.Text.Json.Serialization;

namespace Synapic.Shared.Contracts;

/// <summary>GET /health response (sidecar-protocol.md HealthResponse).</summary>
public record HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "loading";

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    [JsonPropertyName("vram_used_mb")]
    public long? VramUsedMb { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Model entry from GET /models/list (ModelInfo schema).</summary>
public record ModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("task")]
    public string? Task { get; init; }

    [JsonPropertyName("size_mb")]
    public double? SizeMb { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("downloaded")]
    public bool Downloaded { get; init; }
}

/// <summary>POST /models/download body (DownloadRequest schema).</summary>
public record DownloadRequest
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = "main";
}

/// <summary>Inference options inside TagRequest (TagRequest.options schema).</summary>
public record TagOptions
{
    [JsonPropertyName("confidence_threshold")]
    public double ConfidenceThreshold { get; init; } = 0.3;

    [JsonPropertyName("probability_mode")]
    public string ProbabilityMode { get; init; } = "both";

    [JsonPropertyName("probability_threshold")]
    public double ProbabilityThreshold { get; init; } = 0.5;

    [JsonPropertyName("candidate_labels")]
    public string[]? CandidateLabels { get; init; }

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("max_new_tokens")]
    public int MaxNewTokens { get; init; } = 512;
}

/// <summary>POST /tag request (TagRequest schema).</summary>
public record TagRequest
{
    [JsonPropertyName("image_path")]
    public string ImagePath { get; init; } = "";

    [JsonPropertyName("model_id")]
    public string? ModelId { get; init; }

    [JsonPropertyName("task")]
    public string? Task { get; init; }

    [JsonPropertyName("options")]
    public TagOptions? Options { get; init; }
}

/// <summary>One scored keyword from the tier-annotated scoring contract (keyword_scoring.py ScoredKeyword).</summary>
public record ScoredKeyword
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; init; } = "";

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("matched")]
    public bool Matched { get; init; } = true;

    [JsonPropertyName("match_type")]
    public string MatchType { get; init; } = "exact";

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>Tier-annotated scoring result (keyword_scoring.py ScoreResult.to_plain_dict()).</summary>
public record ScoringResult
{
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "unavailable";

    [JsonPropertyName("calibrated")]
    public bool Calibrated { get; init; }

    [JsonPropertyName("scores")]
    public ScoredKeyword[] Scores { get; init; } = Array.Empty<ScoredKeyword>();

    [JsonPropertyName("notes")]
    public string[] Notes { get; init; } = Array.Empty<string>();
}

/// <summary>POST /tag response (TagResponse schema).</summary>
public record TagResponse
{
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("keywords")]
    public string[] Keywords { get; init; } = Array.Empty<string>();

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; init; }

    [JsonPropertyName("scoring")]
    public ScoringResult? Scoring { get; init; }

    [JsonPropertyName("inference_ms")]
    public long InferenceMs { get; init; }

    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; init; }
}

/// <summary>GET /config response and PUT /config body (ConfigDto).</summary>
public record ConfigDto
{
    [JsonPropertyName("model_id")]
    public string? ModelId { get; init; }

    [JsonPropertyName("task")]
    public string? Task { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    [JsonPropertyName("confidence_threshold")]
    public double? ConfidenceThreshold { get; init; }

    [JsonPropertyName("probability_mode")]
    public string? ProbabilityMode { get; init; }

    [JsonPropertyName("probability_threshold")]
    public double? ProbabilityThreshold { get; init; }
}
