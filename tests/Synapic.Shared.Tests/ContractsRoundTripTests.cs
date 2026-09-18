using System.Text.Json;
using Synapic.Shared;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Shared.Tests;

public class ContractsRoundTripTests
{
    private static string Serialize<T>(T value) where T : class
        => JsonSerializer.Serialize(value, SynapicJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"No JsonTypeInfo for {typeof(T).Name}"));

    private static T Deserialize<T>(string json) where T : class
        => (T)(JsonSerializer.Deserialize(json, SynapicJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"No JsonTypeInfo for {typeof(T).Name}"))
            ?? throw new InvalidOperationException("Null result"));

    [Fact]
    public void HealthResponse_UsesSnakeCaseWireNames()
    {
        var json = Serialize(new HealthResponse { Status = "ready", Model = "m", Device = "cuda", VramUsedMb = 123, Error = null });
        Assert.Contains("\"status\":\"ready\"", json);
        Assert.Contains("\"model\":\"m\"", json);
        Assert.Contains("\"device\":\"cuda\"", json);
        Assert.Contains("\"vram_used_mb\":123", json);
        Assert.DoesNotContain("error", json); // WhenWritingNull drops it
    }

    [Fact]
    public void TagRequest_SerializesSnakeCaseWithNestedOptions()
    {
        var request = new TagRequest
        {
            ImagePath = "/tmp/x.jpg",
            ModelId = "LiquidAI/LFM2.5-VL-1.6B",
            Task = "image-text-to-text",
            Options = new TagOptions
            {
                ConfidenceThreshold = 0.4,
                ProbabilityMode = "both",
                ProbabilityThreshold = 0.5,
                CandidateLabels = new[] { "A", "B" },
                SystemPrompt = "be brief",
                MaxNewTokens = 256,
            },
        };
        var json = Serialize(request);
        Assert.Contains("\"image_path\":\"/tmp/x.jpg\"", json);
        Assert.Contains("\"model_id\":\"LiquidAI/LFM2.5-VL-1.6B\"", json);
        Assert.Contains("\"confidence_threshold\":0.4", json);
        Assert.Contains("\"probability_mode\":\"both\"", json);
        Assert.Contains("\"candidate_labels\":[\"A\",\"B\"]", json);
        Assert.Contains("\"max_new_tokens\":256", json);
    }

    [Fact]
    public void TagRequest_DeserializesSidecarJson()
    {
        const string json = """
            {"image_path":"C:/img/cat.jpg","model_id":null,"task":"image-classification",
             "options":{"confidence_threshold":0.3,"probability_mode":"llm","probability_threshold":0.5,
                        "candidate_labels":null,"system_prompt":null,"max_new_tokens":512}}
            """;
        var req = Deserialize<TagRequest>(json);
        Assert.Equal("C:/img/cat.jpg", req.ImagePath);
        Assert.Equal("image-classification", req.Task);
        Assert.NotNull(req.Options);
        Assert.Equal(0.3, req.Options!.ConfidenceThreshold);
        Assert.Equal("llm", req.Options.ProbabilityMode);
    }

    [Fact]
    public void TagResponse_RoundTripsWithScoring()
    {
        var response = new TagResponse
        {
            Category = "Nature",
            Keywords = new[] { "Forest", "Sky" },
            Description = "A forest under a blue sky.",
            Probabilities = new Dictionary<string, double> { ["Forest"] = 0.92, ["Sky"] = 0.61 },
            Scoring = new ScoringResult
            {
                Tier = "label_confidence",
                Calibrated = false,
                Scores = new[] { new ScoredKeyword { Keyword = "Forest", Score = 0.92, MatchType = "exact", Matched = true } },
                Notes = new[] { "note" },
            },
            InferenceMs = 812,
            ModelUsed = "test-model",
        };

        var json = Serialize(response);
        Assert.Contains("\"inference_ms\":812", json);
        Assert.Contains("\"model_used\":\"test-model\"", json);
        Assert.Contains("\"tier\":\"label_confidence\"", json);

        var back = Deserialize<TagResponse>(json);
        Assert.Equal("Nature", back.Category);
        Assert.Equal(2, back.Keywords.Length);
        Assert.Equal(0.92, back.Probabilities!["Forest"]);
        Assert.NotNull(back.Scoring);
        Assert.Equal("label_confidence", back.Scoring!.Tier);
        Assert.Single(back.Scoring.Scores);
        Assert.True(back.Scoring.Scores[0].Matched);
    }

    [Fact]
    public void DownloadRequest_UsesModelIdSnakeCase()
    {
        var json = Serialize(new DownloadRequest { ModelId = "x/y", Revision = "main" });
        Assert.Contains("\"model_id\":\"x/y\"", json);
        Assert.Contains("\"revision\":\"main\"", json);
    }

    [Fact]
    public void ModelInfo_DeserializesSidecarList()
    {
        const string json = """
            [{"id":"LiquidAI/LFM2.5-VL-1.6B","task":"image-text-to-text","size_mb":2048.5,
              "path":"/cache/path","downloaded":true}]
            """;
        var models = Deserialize<ModelInfo[]>(json);
        Assert.Single(models);
        Assert.Equal("LiquidAI/LFM2.5-VL-1.6B", models[0].Id);
        Assert.True(models[0].Downloaded);
        Assert.Equal(2048.5, models[0].SizeMb);
    }
}
