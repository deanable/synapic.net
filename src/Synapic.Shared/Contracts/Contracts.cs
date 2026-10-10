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

    /// <summary>Present while a model download runs (and briefly after it ends).</summary>
    [JsonPropertyName("download")]
    public ModelDownloadProgress? Download { get; init; }
}

/// <summary>
/// Background model download progress (sidecar /health "download" field).
/// status: downloading | complete | failed. TotalBytes is 0 while unknown.
/// </summary>
public record ModelDownloadProgress
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "downloading";

    [JsonPropertyName("done_bytes")]
    public long DoneBytes { get; init; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; init; }

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

    /// <summary>
    /// The tag instruction (Step 2, editable). Null or blank means the sidecar's
    /// built-in <c>DEFAULT_VLM_USER_PROMPT</c> - see <see cref="PromptDefaultsDto"/>.
    /// </summary>
    [JsonPropertyName("user_prompt")]
    public string? UserPrompt { get; init; }

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

    /// <summary>
    /// Why the sidecar had to put the model's reply back together before it
    /// could be read (empty for a reply that parsed as it arrived) — e.g.
    /// <c>missing member separator</c>, <c>truncated payload</c>. A batch whose
    /// replies mostly needed rewriting is reported as repaired rather than
    /// looking like a clean run, so a model that is mangling its own JSON is
    /// visible while the tags are still written.
    ///
    /// Nullable on purpose: the sidecar is a separate binary that can be older
    /// than the app, and one that predates this field sends no member at all —
    /// which this deserializer reads as null, not as the empty array. The host
    /// treats null and empty alike, so an absent field is simply "no repairs".
    /// </summary>
    [JsonPropertyName("reply_repairs")]
    public string[]? ReplyRepairs { get; init; }

    /// <summary>
    /// True when the first reply could not be read as JSON and the sidecar asked
    /// the model once more before giving up on the format (the tags come from
    /// whichever of the two replies read better). The item still succeeded — the
    /// host reports the retry so a batch that needed a second ask per image is
    /// visible rather than looking like a clean run.
    /// </summary>
    [JsonPropertyName("reply_retried")]
    public bool ReplyRetried { get; init; }
}

/// <summary>
/// GET /prompt response (PromptDefaultsDto): the instruction built into the
/// sidecar, which /tag sends whenever the request has no <c>user_prompt</c>.
/// Step 2 loads it so the editable box can start from the shipped wording, and
/// so the sidecar stays the only copy of it.
/// </summary>
public record PromptDefaultsDto
{
    [JsonPropertyName("default_user_prompt")]
    public string DefaultUserPrompt { get; init; } = "";
}

/// <summary>Inference options inside UpscaleRequest (UpscaleRequest.options schema).</summary>
public record UpscaleOptions
{
    /// <summary>quality | balanced | fast — the original app's three workflows.</summary>
    [JsonPropertyName("workflow")]
    public string Workflow { get; init; } = "quality";

    /// <summary>2 or 4 (quality); 2 or 4 (balanced, runs 4x then resizes); any &gt;= 2 for fast.</summary>
    [JsonPropertyName("factor")]
    public int Factor { get; init; } = 2;

    /// <summary>auto | fp16 | fp32 (quality/balanced on CUDA).</summary>
    [JsonPropertyName("precision")]
    public string Precision { get; init; } = "auto";

    /// <summary>0.0–1.0 blend toward Lanczos (balanced only; 1.0 = pure AI output).</summary>
    [JsonPropertyName("denoise_strength")]
    public double DenoiseStrength { get; init; } = 1.0;

    /// <summary>Unsharp mask amount, 0.0 = off (0.0–2.0).</summary>
    [JsonPropertyName("sharpen_amount")]
    public double SharpenAmount { get; init; }

    /// <summary>
    /// Maximum dimension (width or height) of the input image before upscaling.
    /// Images larger than this are downscaled to fit before being fed to the
    /// upscaler. The model works on 64×64 patches, so feeding it a 16000×12000
    /// image does not improve quality — it only wastes memory and time on the
    /// pre/post-processing, and would multiply an already-huge output. Pass 0 to
    /// disable (feed the full image). The saved output is always the
    /// (possibly capped) input size × the factor, so an oversized source yields
    /// a proportionally smaller result rather than a much larger one.
    /// </summary>
    [JsonPropertyName("max_dimension")]
    public int MaxDimension { get; init; } = 2048;

    /// <summary>keep | JPEG | PNG | WEBP.</summary>
    [JsonPropertyName("output_format")]
    public string OutputFormat { get; init; } = "keep";

    [JsonPropertyName("jpeg_quality")]
    public int JpegQuality { get; init; } = 95;

    [JsonPropertyName("overwrite_existing")]
    public bool OverwriteExisting { get; init; } = true;
}

/// <summary>POST /upscale request (UpscaleRequest schema).</summary>
public record UpscaleRequest
{
    [JsonPropertyName("image_path")]
    public string ImagePath { get; init; } = "";

    /// <summary>Optional explicit target; null writes <c>{stem}_upscaled{ext}</c> beside the input.</summary>
    [JsonPropertyName("output_path")]
    public string? OutputPath { get; init; }

    [JsonPropertyName("options")]
    public UpscaleOptions? Options { get; init; }
}

/// <summary>POST /upscale response (UpscaleResponse schema).</summary>
public record UpscaleResponse
{
    [JsonPropertyName("output_path")]
    public string OutputPath { get; init; } = "";

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("original_width")]
    public int OriginalWidth { get; init; }

    [JsonPropertyName("original_height")]
    public int OriginalHeight { get; init; }

    [JsonPropertyName("workflow")]
    public string Workflow { get; init; } = "quality";

    [JsonPropertyName("factor")]
    public int Factor { get; init; }

    /// <summary>Hugging Face model id used; null for the fast (Lanczos) workflow.</summary>
    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; init; }

    [JsonPropertyName("inference_ms")]
    public long InferenceMs { get; init; }
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
