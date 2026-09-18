# Sidecar HTTP Protocol (v1.0, frozen)

The C# host and Python sidecar communicate over HTTP/JSON on `127.0.0.1`.
This document mirrors README §4.2 and is the CI contract baseline.

- Base URL: `http://127.0.0.1:{port}` — port assigned by the OS at launch and
  communicated via the **port file**: `%TEMP%/synapic_port_{pid}.txt`
  containing `port\npid\n`.
- All request/response bodies are JSON with `snake_case` keys.
- The Avalonia side serializes with System.Text.Json source generation
  (`SynapicJsonContext`); the sidecar validates with Pydantic.

## GET /health
Readiness probe; polled up to 120 s at startup.

```json
{
  "status": "loading" | "ready" | "error",
  "model": "LiquidAI/LFM2.5-VL-1.6B | null",
  "device": "cuda | mps | cpu | null",
  "vram_used_mb": 1234,
  "error": "message | null"
}
```

## GET /models/list
```json
[ { "id": "…", "task": "image-text-to-text", "size_mb": 2048.5,
    "path": "…", "downloaded": true } ]
```

## POST /models/download
Request `{"model_id": "org/model", "revision": "main"}`.
Responses: `200` (started / already downloaded), `202`-style
`already_downloading`, `422` for incompatible models (GPTQ/AWQ/GGUF/…).

## POST /tag
Request:
```json
{
  "image_path": "C:/img/x.jpg",
  "model_id": "LiquidAI/LFM2.5-VL-1.6B",      // optional; overrides session
  "task": "image-text-to-text",                // image-classification | zero-shot-image-classification | image-to-text | image-text-to-text
  "options": {
    "confidence_threshold": 0.3,
    "probability_mode": "both",                // llm | probability | both
    "probability_threshold": 0.5,
    "candidate_labels": ["a", "b"],
    "system_prompt": "…",
    "max_new_tokens": 512
  }
}
```
Responses:
- `200` TagResponse (below)
- `404` image not found
- `422` validation (bad task, empty path)
- `503` model loading — the host retries once

TagResponse:
```json
{
  "category": "Nature",
  "keywords": ["Forest", "Sky"],
  "description": "…",
  "probabilities": {"Forest": 0.92},
  "scoring": {
    "tier": "logprob | label_confidence | embedding | semantic_json | unavailable",
    "calibrated": false,
    "scores": [{"keyword": "Forest", "score": 0.92, "matched": true, "match_type": "exact", "note": ""}],
    "notes": ["…"]
  },
  "inference_ms": 812,
  "model_used": "…"
}
```

## GET /config | PUT /config
Session inference config: `model_id`, `task`, `device`,
`confidence_threshold`, `probability_mode`, `probability_threshold`.
PUT with a changed `model_id` unloads the current model.

## POST /shutdown
Graceful exit: returns `{"status": "shutting_down"}` then the process exits.
The host additionally kills the process tree after a 5 s grace period.

## Contract enforcement
`tests/Synapic.Inference.Tests/test_service_contract.py` exercises the served
routes (status codes + shapes) in CI; the C# side is pinned by
`tests/Synapic.Shared.Tests/ContractsRoundTripTests.cs`. Any wire-format
change must update both plus this document.
