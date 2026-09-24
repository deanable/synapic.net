<!-- GENERATED FILE - DO NOT EDIT BY HAND. -->
<!-- Regenerate: python build/generate-protocol-doc.py -->
<!-- Verify in CI: python build/generate-protocol-doc.py --check -->

# Sidecar HTTP Protocol (v1.0.0)

The contract between the Avalonia host and the Python sidecar. This file is
**generated** from the code and verified in CI, so it cannot drift:

- Routes, request bodies, validation constraints and documented status codes
  come from the live FastAPI OpenAPI schema of
  `src/Synapic.Inference/service.py`.
- Response shapes come from the C# contract DTOs the host deserializes in
  `src/Synapic.Shared/Contracts/Contracts.cs`.
- The generator cross-checks request DTOs across both languages and fails on
  any field mismatch (see [Drift checks](#drift-checks)).

## Transport

- **Base URL**: `http://127.0.0.1:{port}` (loopback only).
- **Port**: OS-assigned at launch (`--port=0`) and announced through the port
  file `%TEMP%/synapic_port_{pid}.txt`, which contains `port\npid\n`. The host
  reads it (validating the pid) before polling `/health`.
- **Encoding**: JSON with `snake_case` keys. The host serialises with the
  source-generated `SynapicJsonContext`; the sidecar validates with Pydantic.
- **Timeouts**: the host allows 5 minutes per `/tag` and retries once on `503`
  (model loading). `/health` is polled until `ready`, max 120 s.

## Routes

### `GET /config`

Read the session inference config.

**Responses**

- `200` — Current session inference config (ConfigDto).

**`200` body** — `ConfigDto` (C# `Synapic.Shared.Contracts.ConfigDto`)

| field | type | optional |
|-------|------|----------|
| `model_id` | string | yes |
| `task` | string | yes |
| `device` | string | yes |
| `confidence_threshold` | number | yes |
| `probability_mode` | string | yes |
| `probability_threshold` | number | yes |

### `PUT /config`

Update the session inference config (a changed `model_id` unloads the model).

**Request** (`application/json`, required) — `ConfigModel`

| field | type | required | constraints |
|-------|------|----------|-------------|
| `model_id` | string (nullable) | no | — |
| `task` | string (nullable) | no | — |
| `device` | string (nullable) | no | — |
| `confidence_threshold` | number (nullable) | no | — |
| `probability_mode` | string (nullable) | no | — |
| `probability_threshold` | number (nullable) | no | — |

**Responses**

- `200` — Updated session inference config (ConfigDto).
- `422` — Validation error (unsupported task).

**`200` body** — `ConfigDto` (C# `Synapic.Shared.Contracts.ConfigDto`)

| field | type | optional |
|-------|------|----------|
| `model_id` | string | yes |
| `task` | string | yes |
| `device` | string | yes |
| `confidence_threshold` | number | yes |
| `probability_mode` | string | yes |
| `probability_threshold` | number | yes |

### `GET /health`

Readiness probe, polled up to 120 s at startup.

**Responses**

- `200` — Readiness, loaded model/device and any active model download (HealthResponse).

**`200` body** — `HealthResponse` (C# `Synapic.Shared.Contracts.HealthResponse`)

| field | type | optional |
|-------|------|----------|
| `status` | string | no |
| `model` | string | yes |
| `device` | string | yes |
| `vram_used_mb` | integer | yes |
| `error` | string | yes |
| `download` | ModelDownloadProgress | yes |

### `POST /models/download`

Start a background model download.

**Request** (`application/json`, required) — `DownloadRequestModel`

| field | type | required | constraints |
|-------|------|----------|-------------|
| `model_id` | string | yes | — |
| `revision` | string | no | default `main` |

**Responses**

- `200` — Download outcome: status is download_started | already_downloading | downloaded, plus model_id.
- `422` — Model is an unsupported (quantized) format.

**`200` body**

| field | type | notes |
|-------|------|-------|
| `status` | string | `download_started` / `already_downloading` / `downloaded` |
| `model_id` | string | — |

### `GET /models/list`

Models present in the HF cache (`HF_HOME`).

**Responses**

- `200` — Models cached in HF_HOME (ModelInfo[]).
- `500` — Cache scan failed.

**`200` body** — array of `ModelInfo` (C# `Synapic.Shared.Contracts.ModelInfo[]`)

| field | type | optional |
|-------|------|----------|
| `id` | string | no |
| `task` | string | yes |
| `size_mb` | number | yes |
| `path` | string | yes |
| `downloaded` | boolean | no |

### `GET /prompt`

The tag instruction built into this sidecar, which `/tag` uses when the request has no `user_prompt`.

**Responses**

- `200` — The built-in tag instruction (PromptDefaultsDto): what /tag sends when the request has no user_prompt.

**`200` body** — `PromptDefaultsDto` (C# `Synapic.Shared.Contracts.PromptDefaultsDto`)

| field | type | optional |
|-------|------|----------|
| `default_user_prompt` | string | no |

### `POST /shutdown`

Graceful exit; the host also kills the process tree after a grace period.

**Responses**

- `200` — `{status: shutting_down}` - the process exits.

**`200` body**

| field | type | notes |
|-------|------|-------|
| `status` | string | always `"shutting_down"` |

### `POST /tag`

Run inference on one image and return its tags.

**Request** (`application/json`, required) — `TagRequestModel`

| field | type | required | constraints |
|-------|------|----------|-------------|
| `image_path` | string | yes | — |
| `model_id` | string (nullable) | no | — |
| `task` | string (nullable) | no | — |
| `options` | TagOptionsModel (nullable) | no | — |

**Responses**

- `200` — Inference result (TagResponse): category, keywords, description, probabilities, optional scoring, inference_ms, model_used.
- `404` — Image not found.
- `422` — Validation error (blank image_path, bad task).
- `503` — Timed out waiting for an in-flight model load; the host retries once.

**`200` body** — `TagResponse` (C# `Synapic.Shared.Contracts.TagResponse`)

| field | type | optional |
|-------|------|----------|
| `category` | string | yes |
| `keywords` | array of string | no |
| `description` | string | yes |
| `probabilities` | object (string → number) | yes |
| `scoring` | ScoringResult | yes |
| `inference_ms` | integer | no |
| `model_used` | string | yes |

## Schemas

Wire shapes of every DTO used above (nested DTOs included).

### `HealthResponse`

| field | type | optional |
|-------|------|----------|
| `status` | string | no |
| `model` | string | yes |
| `device` | string | yes |
| `vram_used_mb` | integer | yes |
| `error` | string | yes |
| `download` | ModelDownloadProgress | yes |

### `ModelDownloadProgress`

| field | type | optional |
|-------|------|----------|
| `model_id` | string | no |
| `status` | string | no |
| `done_bytes` | integer | no |
| `total_bytes` | integer | no |
| `error` | string | yes |

### `ModelInfo`

| field | type | optional |
|-------|------|----------|
| `id` | string | no |
| `task` | string | yes |
| `size_mb` | number | yes |
| `path` | string | yes |
| `downloaded` | boolean | no |

### `ConfigDto`

| field | type | optional |
|-------|------|----------|
| `model_id` | string | yes |
| `task` | string | yes |
| `device` | string | yes |
| `confidence_threshold` | number | yes |
| `probability_mode` | string | yes |
| `probability_threshold` | number | yes |

### `PromptDefaultsDto`

| field | type | optional |
|-------|------|----------|
| `default_user_prompt` | string | no |

### `TagResponse`

| field | type | optional |
|-------|------|----------|
| `category` | string | yes |
| `keywords` | array of string | no |
| `description` | string | yes |
| `probabilities` | object (string → number) | yes |
| `scoring` | ScoringResult | yes |
| `inference_ms` | integer | no |
| `model_used` | string | yes |

### `ScoringResult`

| field | type | optional |
|-------|------|----------|
| `tier` | string | no |
| `calibrated` | boolean | no |
| `scores` | array of ScoredKeyword | no |
| `notes` | array of string | no |

### `ScoredKeyword`

| field | type | optional |
|-------|------|----------|
| `keyword` | string | no |
| `score` | number | no |
| `matched` | boolean | no |
| `match_type` | string | no |
| `note` | string | yes |

### `TagRequest`

| field | type | optional |
|-------|------|----------|
| `image_path` | string | no |
| `model_id` | string | yes |
| `task` | string | yes |
| `options` | TagOptions | yes |

### `TagOptions`

| field | type | optional |
|-------|------|----------|
| `confidence_threshold` | number | no |
| `probability_mode` | string | no |
| `probability_threshold` | number | no |
| `candidate_labels` | array of string | yes |
| `system_prompt` | string | yes |
| `user_prompt` | string | yes |
| `max_new_tokens` | integer | no |

### `DownloadRequest`

| field | type | optional |
|-------|------|----------|
| `model_id` | string | no |
| `revision` | string | no |

## Drift checks

`python build/generate-protocol-doc.py --check` runs in CI and fails when:

- a request DTO differs between the FastAPI model and the C# record:
  - `TagRequestModel` ↔ `TagRequest` (POST /tag body)
  - `TagOptionsModel` ↔ `TagOptions` (POST /tag options)
  - `DownloadRequestModel` ↔ `DownloadRequest` (POST /models/download body)
  - `ConfigModel` ↔ `ConfigDto` (PUT /config body)
- a response DTO referenced here is missing from `Contracts.cs`;
- this document is stale (the generated text differs from the committed file).

Behavioural guarantees (status codes, `/health` shape, `/config` merge) are
additionally pinned by `tests/Synapic.Inference.Tests/test_service_contract.py`
and `tests/Synapic.Shared.Tests/ContractsRoundTripTests.cs`.
