# Python Sidecar Reference (`src/Synapic.Inference`)

The inference engine, packaged as a single PyInstaller executable and driven
over local HTTP. Implements spec §4 and the frozen contract in
[`sidecar-protocol.md`](sidecar-protocol.md). Inference logic is a faithful
port of the original Python app — only the I/O boundary changed (file paths /
UI queues → HTTP requests and response dicts).

Modules use **flat absolute imports** (`import config`, `import model_loader`)
because `service.py` is the PyInstaller entry script and has no parent package;
pytest imports the same modules top-level so there is one import identity
between the app and the tests.

---

## `config.py` — constants

- `HF_CACHE_DIR` — resolved from `huggingface_hub.constants`
  (`HUGGINGFACE_HUB_CACHE`), honouring `HF_HOME` set by the host.
- `DEFAULT_MODEL_ID = "LiquidAI/LFM2.5-VL-450M"`.
- `AUTO_DOWNLOAD_DISABLE_ENV = "SYNAPIC_DISABLE_AUTO_DOWNLOAD"` — tests/offline
  CI set `=1` to skip the startup download.
- Task names (`MODEL_TASK_*`, `VALID_TASKS`), display/capability maps.
- `DEFAULT_CANDIDATE_LABELS`, `MAX_IMAGE_SIZE_MB`, `MAX_KEYWORDS_PER_IMAGE`.
- `PORT_FILE_ENV_VAR = "SYNAPIC_PORT_FILE"`.
- `MODEL_FILE_EXCLUSIONS`, `INCOMPATIBLE_MODEL_PATTERNS` (gptq/AWQ/GGUF/EXL2/
  bitsandbytes/int4/int8…).
- `STOP_WORDS` (keyword extraction).

---

## `service.py` — FastAPI app

At import time it makes stdout/stderr safe for packaged `pythonw` (replaces
`None` with `os.devnull`) and keeps HF progress bars enabled so byte-level
download progress reaches `/health`.

### Request models (Pydantic)

`TagOptionsModel` (`confidence_threshold`, `probability_mode` ∈
`llm|probability|both`, `probability_threshold`, `candidate_labels`,
`system_prompt`, `max_new_tokens` 1–4096), `TagRequestModel` (`image_path`,
optional `model_id`/`task`/`options`), `DownloadRequestModel`, `ConfigModel`.

### Session config

`_session_config` (guarded by `_config_lock`) holds the current `model_id`,
`task`, `device`, `confidence_threshold`, `probability_mode`,
`probability_threshold`. Defaults come from `config`.

### Routes

| Route | Behaviour |
|-------|-----------|
| `GET /health` | State snapshot (`status` loading/ready/error, `model`, `device`, `vram_used_mb`, `error`) plus a `download` object while a download is running/recently finished. |
| `GET /models/list` | Local HF-cache scan (`find_local_models`): id, task, size_mb, path, downloaded. |
| `POST /models/download` | 422 for incompatible models; `downloaded` if already present; `already_downloading` if a thread is alive; else spawns a daemon thread and returns `download_started`. |
| `POST /tag` | Validates path (422/404), 503 while status is `loading`, loads/uses the model, runs `inference_engine.run_inference`, returns the TagResponse dict. Updates the session model when the request overrides it. |
| `GET /config` | Current session config. |
| `PUT /config` | Merges non-null fields; a changed `model_id` calls `model_loader.unload_model()`; validates `task`. |
| `POST /shutdown` | Sets the shutdown event and a daemon thread exits the process via `os._exit(0)` after a 0.5 s flush delay. |

### Lifespan / startup

A daemon thread `_ensure_default_model` checks whether `DEFAULT_MODEL_ID` is in
the cache and downloads it in the background if not (skipped when
`SYNAPIC_DISABLE_AUTO_DOWNLOAD=1`). The model is **not** baked into installers.

### Entry point (`main`)

Parses `--port` (default 0) and `--host` (default 127.0.0.1), **pre-binds the
socket** so the OS-assigned port is known before serving, writes
`port\npid\n` to the `SYNAPIC_PORT_FILE`, then runs uvicorn against the
pre-bound socket. Single process, no `workers=`; `/tag` is a sync `def`, so
requests execute on Starlette's AnyIO threadpool.

---

## `model_loader.py` — models, downloads, scoring

### Runtime state (`/health`)

`_state` = `{status, model, device, vram_used_mb, error}` behind a lock.
`set_status`, `set_loaded_model` (also refreshes VRAM/RSS), `get_state_snapshot`,
`_read_vram_used_mb` (CUDA `mem_get_info` → psutil RSS → `None`).

### Download progress

`_DOWNLOAD_PROGRESS[model_id]` tracks `{status, done, total, expected_total,
error, finished_at}`. Helpers: `mark_download_started|complete|failed`,
`get_download_progress`, `reset_download_state` (test helper),
`get_active_download` — surfaces the in-flight download, else one that
finished within a 30 s TTL, as the `/health` `download` object.

### Device detection

`get_device_info()` probes CPU/CUDA/MPS and returns devices, a default, and a
`debug_info` block (torch/cuda versions). `_resolve_device_str` falls back to
CPU with a warning; `_torch_device_arg` maps `cpu → -1`.

### Compatibility

`is_model_compatible` / `get_incompatibility_reason` reject quantized formats
that need unbundled libraries.

### Cache paths & presence

`get_model_cache_dir` (`models--org--name`), `_get_latest_snapshot_path`,
`is_model_downloaded` — a model counts as downloaded when a snapshot has both
`config.json` and a weight file (`.safetensors/.bin/.pt/.pth/.onnx/.gguf`).

### Task suggestion

`get_suggested_task(config_dict)` from `pipeline_tag` / `architectures` /
`model_type`; `get_model_capability(task)` for display.

### Local discovery

`find_local_models()` walks `HF_CACHE_DIR`, skips incompatible ids, and reports
id/task/size_mb/path/downloaded.

### Download

`_total_repo_bytes` asks the Hub for per-file sizes (best effort; used for a
real percentage). `download_model` uses `snapshot_download` with a tqdm
subclass that mirrors `(n, total)` into the progress registry; already-downloaded
models short-circuit.

### Loading

- `_get_hf_pipeline()` imports `transformers.pipeline` once under a lock
  (avoids a frozen-PyInstaller partial-import race).
- `load_model(model_id, task, device, token)` — resolves the device, returns a
  cached `(pipeline, task)` on hit, else marks `loading` and constructs under
  `_model_build_lock` (double-checked).
- `_construct_model` — downloads when missing, prefers the local snapshot
  path, applies the classification ⇄ image-to-text task swap, builds
  `pipeline(resolved_task, device=…, dtype="auto",
  model_kwargs={"low_cpu_mem_usage": True})`, keeps **at most one** pipeline
  resident (clears the cache), and records the loaded model.
- `unload_model()` drops the cache and sets `loading`.

### Scoring (tier ladder)

Only `image-classification` pipelines can be scored locally; the CLIP tier is
opt-in via `embedding_rescue_enabled`.

- `run_local_label_confidence_inference(model, image_path, candidates)` —
  requires an `image-classification` pipeline; requests every label
  (`top_k = num_labels`), maps candidates to labels by exact match then fuzzy
  matching (`_fuzzy_match_label`, `difflib` ratio ≥ 0.6, substring ≥ 0.45),
  unmatched → 0.0; raises `ValueError` with the model's label sample when
  nothing matches.
- `TransformersCLIPScorer` — tier 2.5, `openai/clip-vit-base-patch32`,
  temperature 0.01; lazy/cached load, text features cached per candidate set,
  true cosine similarities.
- `pick_scoring_tier(provider, task, candidates, mode)` — returns
  `LABEL_CONFIDENCE` for a local image-classification model with candidates and
  a non-`llm` mode, otherwise `None` (so VLMs are never scored — they fall
  through to LLM tagging).
- `score_keywords(...)` — runs the ladder, never raises for provider/parse
  failures (degrades to an `unavailable_result` with a note; may fall back to
  the embedding tier when enabled).
- `score_keywords_embedding(...)` — softmaxed CLIP similarities; the notes
  explain that distribution scores are candidate-set relative (not calibrated).

---

## `inference_engine.py` — `run_inference`

Signature: `run_inference(model, image_path, task, confidence_threshold=0.3,
probability_mode="llm", probability_threshold=0.0, candidate_labels=None,
system_prompt="", max_new_tokens=512, embedding_rescue_enabled=False)`.

Order of operations:

1. **Probability pass** (when mode ≠ `llm` and candidates exist): runs
   `model_loader.score_keywords`; an all-zero `unavailable` result is discarded
   with a warning; otherwise scores are logged, thresholded, and a
   thresholded `scoring` view is built.
2. **Probability-only tagging** (`probability` mode): the top-scoring candidate
   becomes the category and all candidates become keywords; falls back to the
   LLM when scoring produced nothing.
3. **Model inference** by task:
   - `image-text-to-text` (VLM): chat-style `messages`, with the optional
     `system_prompt` as a `system` message, then the image and the fixed
     `DEFAULT_VLM_USER_PROMPT` (asks for JSON with `description`, `category`,
     `keywords`).
   - `image-to-text`: prompted caption ("Describe the image.") with a plain
     fallback.
   - `zero-shot-image-classification`: candidates (or
     `DEFAULT_CANDIDATE_LABELS`).
   - otherwise: plain image classification.
4. **Extraction** via `tag_extractor.extract_tags_from_result`, then scored
   probabilities are merged over the extracted ones, a `[AI: No Result]`
   placeholder is used when nothing was extracted, and `_finalize` caps
   keywords at `MAX_KEYWORDS_PER_IMAGE` and returns
   `{category, keywords, description, probabilities, inference_ms,
   model_used, scoring?}`.

### `_generation_kwargs(model, max_new_tokens)`

Builds the `generate_kwargs` for both generation paths. Many VLM repos ship a
`generation_config` with `max_length` set (LFM2.5-VL declares `max_length=20`),
and passing `max_new_tokens` next to it made transformers log two warnings on
every image (`generation_config` with generation-related arguments; and
`max_new_tokens` vs `max_length`). The helper clones the pipeline's own
generation config, pins `max_new_tokens`, clears `max_length`, and returns
`{"generation_config": clone}` — a single source of truth, same precedence,
no warnings. It falls back to the explicit kwarg for pipeline shapes it does
not recognise.

---

## `tag_extractor.py` — raw output → tags

`extract_tags_from_result(result, model_task, threshold, stop_words,
probabilities) → (category, keywords, description, probabilities)`:

- **image-classification**: top-5 labels above threshold become keywords.
- **zero-shot**: best label above threshold becomes the category.
- **image-to-text / image-text-to-text**: pulls `generated_text` (plain or the
  assistant message of a chat list); if it is already a dict it is used
  directly, otherwise `json_utils.extract_dict_from_text` is tried with the
  expected keys; when that fails the whole text becomes the description after
  stripping known prefixes, and the category falls back to `[AI: No Result]`.
- `_sanitize_category` normalises string/list/malformed categories;
  `_normalize_keywords` flattens strings/lists; `to_title_case` applies the
  original Title-Case rules (compound words, underscores, slashes, all-caps,
  mixed case).

---

## `json_utils.py` — safe literal parsing

`safe_parse_python_literal` (length/depth guarded JSON → `ast.literal_eval`),
`extract_dict_from_text` (fenced code blocks → balanced-brace scan → truncated
payload repair), with bracketed-string awareness to avoid false delimiters.

---

## `keyword_scoring.py` — tier contract + math

Dependency-free core shared by every tier.

- `SCORING_TIER` = `logprob | label_confidence | embedding | semantic_json |
  unavailable`.
- `ScoredKeyword(keyword, score, matched, match_type, note)` and
  `ScoreResult(scores, tier, calibrated, notes)` with `score_map` and
  `to_plain_dict()` (the exact `scoring` payload the host consumes).
- `softmax_from_logprobs` (calibrated), `softmax_from_similarities`
  (candidate-set relative), `normalize_json_probabilities`,
  `apply_threshold`, `build_score_result`, `build_thresholded_view`,
  `unavailable_result`, and a final `_enforce_sum_to_one` float-safety pass.

---

## Build & packaging

- `build/fetch-python.*` downloads python-build-standalone 3.11 into
  `build/_python/<rid>/`.
- `build/install-python-deps.*` pip-installs `requirements.txt` (CPU torch by
  default; the `-cuda` RID suffix swaps in the CUDA wheel index).
- `build/build-sidecar.*` runs PyInstaller against
  `synapic-inference.spec` and writes `artifacts/<rid>/synapic-inference(.exe)`.
- `build/build-server.*` chains the three (used by the app's Build Server
  button and CI).
- CI additionally runs `build/check-lfm2vl-tie.py` before packaging to fail
  fast on a bad transformers pin.
