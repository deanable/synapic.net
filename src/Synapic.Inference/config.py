"""Sidecar configuration constants.

Ported from the Python Synapic app's ``src/core/config.py`` with UI-specific
constants removed and inference-relevant constants preserved unchanged, per the
migration spec §4.3 (keep inference logic as close to current logic as
possible; only adapt I/O boundaries).
"""

from __future__ import annotations

import os

# ============================================================================
# HUGGING FACE CACHE CONFIGURATION
# ============================================================================
# The sidecar honors HF_HOME, which the Avalonia host sets at launch
# (%LOCALAPPDATA%/Synapic/models on Windows, ~/.cache/synapic/models on
# Linux/macOS — spec §6.2). Fall back to the standard HF location.

try:
    from huggingface_hub import constants as _hf_constants

    HF_CACHE_DIR = _hf_constants.HUGGINGFACE_HUB_CACHE
except Exception:  # pragma: no cover - fallback when hub isn't importable
    HF_CACHE_DIR = os.path.join(
        os.path.expanduser("~"), ".cache", "huggingface", "hub"
    )

# ============================================================================
# MODEL DISCOVERY
# ============================================================================

MODEL_SEARCH_LIMIT = 20

# ============================================================================
# DEFAULT VISION MODEL
# ============================================================================

# The default local vision model. NOT baked into the application bundle
# (that would inflate every installer by ~1 GB): on server startup the
# sidecar checks the HF cache (HF_HOME) and downloads it in the background
# when absent, surfacing byte-level progress through /health's "download"
# field for the UI indicator.
DEFAULT_MODEL_ID = "LiquidAI/LFM2.5-VL-450M"

# Auto-download opt-out (used by tests and offline CI):
# set SYNAPIC_DISABLE_AUTO_DOWNLOAD=1 to skip the startup check/download.
AUTO_DOWNLOAD_DISABLE_ENV = "SYNAPIC_DISABLE_AUTO_DOWNLOAD"

# ============================================================================
# ZERO-SHOT CLASSIFICATION DEFAULTS
# ============================================================================

DEFAULT_CANDIDATE_LABELS = [
    "photography", "art", "nature", "portrait", "landscape", "urban",
    "people", "animal",
]

# ============================================================================
# MODEL TASK TYPES
# ============================================================================

MODEL_TASK_IMAGE_CLASSIFICATION = "image-classification"
MODEL_TASK_ZERO_SHOT = "zero-shot-image-classification"
MODEL_TASK_IMAGE_TO_TEXT = "image-to-text"
MODEL_TASK_IMAGE_TEXT_TO_TEXT = "image-text-to-text"

# Tasks accepted on /tag (spec §4.2 TagRequest.task)
VALID_TASKS = {
    MODEL_TASK_IMAGE_CLASSIFICATION,
    MODEL_TASK_ZERO_SHOT,
    MODEL_TASK_IMAGE_TO_TEXT,
    MODEL_TASK_IMAGE_TEXT_TO_TEXT,
}

TASK_DISPLAY_MAP = {
    MODEL_TASK_IMAGE_CLASSIFICATION: "Keywords (Auto)",
    MODEL_TASK_ZERO_SHOT: "Categories (Custom)",
    MODEL_TASK_IMAGE_TO_TEXT: "Description",
}

CAPABILITY_MAP = {
    MODEL_TASK_IMAGE_CLASSIFICATION: "Keywording",
    MODEL_TASK_ZERO_SHOT: "Categorisation",
    MODEL_TASK_IMAGE_TO_TEXT: "Description",
    MODEL_TASK_IMAGE_TEXT_TO_TEXT: "Multi-modal",
    "visual-question-answering": "Multi-modal",
}

# ============================================================================
# IMAGE PROCESSING LIMITS
# ============================================================================

MAX_IMAGE_SIZE_MB = 50
MAX_KEYWORDS_PER_IMAGE = 20

# ============================================================================
# PORT FILE PROTOCOL (C# ↔ Python contract, spec §2)
# ============================================================================

PORT_FILE_ENV_VAR = "SYNAPIC_PORT_FILE"

# ============================================================================
# MODEL DOWNLOAD CONFIGURATION
# ============================================================================

MODEL_FILE_EXCLUSIONS = (".gitattributes", "README.md")

INCOMPATIBLE_MODEL_PATTERNS = [
    "-gptq", "-awq", "-gguf", "-ggml", "-exl2", "-bnb", "-4bit", "-8bit",
    "int4", "int8",
]

# ============================================================================
# KEYWORD EXTRACTION STOP WORDS
# ============================================================================

STOP_WORDS = [
    "a", "about", "above", "after", "again", "against", "all", "am", "an",
    "and", "any", "are", "aren't", "as", "at",
    "be", "because", "been", "before", "being", "below", "between", "both",
    "but", "by", "can't", "cannot", "could",
    "couldn't", "did", "didn't", "do", "does", "doesn't", "doing", "don't",
    "down", "during", "each", "few", "for",
    "from", "further", "had", "hadn't", "has", "hasn't", "have", "haven't",
    "having", "he", "he'd", "he'll", "he's",
    "her", "here", "here's", "hers", "herself", "him", "himself", "his",
    "how", "how's", "i", "i'd", "i'll", "i'm",
    "i've", "if", "in", "into", "is", "isn't", "it", "it's", "its", "itself",
    "let's", "me", "more", "most",
    "mustn't", "my", "myself", "no", "nor", "not", "of", "off", "on", "once",
    "only", "or", "other", "ought", "our",
    "ours", "ourselves", "out", "over", "own", "same", "shan't", "she",
    "she'd", "she'll", "she's", "should",
    "shouldn't", "so", "some", "such", "than", "that", "that's", "the",
    "their", "theirs", "them", "themselves",
    "then", "there", "there's", "these", "they", "they'd", "they'll",
    "they're", "they've", "this", "those",
    "through", "to", "too", "under", "until", "up", "very", "was", "wasn't",
    "we", "we'd", "we'll", "we're",
    "we've", "were", "weren't", "what", "what's", "when", "when's", "where",
    "where's", "which", "while", "who",
    "who's", "whom", "why", "why's", "with", "won't", "would", "wouldn't",
    "you", "you'd", "you'll", "you're",
    "you've", "your", "yours", "yourself", "yourselves",
]
