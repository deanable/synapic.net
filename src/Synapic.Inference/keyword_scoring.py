"""Tier-agnostic keyword scoring core.

Faithful port of the Python Synapic app's ``src/core/keyword_scoring.py``
(dependency-free math + result contract shared by every scoring tier).
Behavior is intentionally identical: same softmax constructions, same
sum-to-one enforcement, same thresholding semantics.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from enum import Enum
from typing import Dict, List, Optional

# Tolerance for the sum-to-one invariant.
SUM_TO_ONE_TOLERANCE = 1e-9


class SCORING_TIER(str, Enum):
    """Which scoring path produced a ScoreResult."""

    LOGPROB = "logprob"
    LABEL_CONFIDENCE = "label_confidence"
    EMBEDDING = "embedding"
    SEMANTIC_JSON = "semantic_json"
    UNAVAILABLE = "unavailable"


@dataclass
class ScoredKeyword:
    """One user keyword and its score under a given tier."""

    keyword: str
    score: float
    matched: bool = True
    # "exact" | "fuzzy" | "none" | "llm" | "semantic"
    match_type: str = "exact"
    note: str = ""


@dataclass
class ScoreResult:
    """Tier-agnostic result contract for keyword scoring."""

    scores: List[ScoredKeyword] = field(default_factory=list)
    tier: SCORING_TIER = SCORING_TIER.UNAVAILABLE
    # True ONLY when scores form a calibrated probability distribution over
    # the candidate set (currently: the logprob tier).
    calibrated: bool = False
    notes: List[str] = field(default_factory=list)

    @property
    def score_map(self) -> Dict[str, float]:
        """Plain {keyword: score} mapping in original candidate order."""
        return {s.keyword: s.score for s in self.scores}

    def to_plain_dict(self) -> dict:
        """JSON-serializable form for the /tag response `scoring` field."""
        return {
            "tier": self.tier.value,
            "calibrated": self.calibrated,
            "scores": [
                {
                    "keyword": s.keyword,
                    "score": s.score,
                    "matched": s.matched,
                    "match_type": s.match_type,
                    "note": s.note,
                }
                for s in self.scores
            ],
            "notes": list(self.notes),
        }


def softmax_from_logprobs(
    logprob_map: Dict[str, float], candidates: List[str]
) -> Dict[str, float]:
    """Softmax raw first-token logprobs into a calibrated distribution.

    - candidates missing from ``logprob_map`` are treated as -inf;
    - the maximum logprob is subtracted before exponentiation;
    - when every candidate is -inf the result falls back to uniform;
    - returned values sum to 1.0 within SUM_TO_ONE_TOLERANCE.
    """
    if not candidates:
        return {}

    filled = {c: float(logprob_map.get(c, float("-inf"))) for c in candidates}
    max_logprob = max(filled.values())

    if max_logprob == float("-inf"):
        uniform = 1.0 / len(candidates)
        return {c: uniform for c in candidates}

    exp_probs = {c: math.exp(v - max_logprob) for c, v in filled.items()}
    total = math.fsum(exp_probs.values())
    if total <= 0.0:
        uniform = 1.0 / len(candidates)
        return {c: uniform for c in candidates}

    normalized = {c: v / total for c, v in exp_probs.items()}
    return _enforce_sum_to_one(normalized)


def softmax_from_similarities(
    similarities: Dict[str, float], candidates: List[str], temperature: float = 0.01
) -> Dict[str, float]:
    """Softmax CLIP-style cosine similarities over the candidate set.

    Standard contrastive zero-shot construction: cosine similarity scaled by
    1/temperature, then normalized. NOT calibrated (candidate-set relative).
    """
    if not candidates:
        return {}

    if temperature <= 0.0:
        temperature = 0.01

    sims = {c: float(similarities.get(c, -1.0)) for c in candidates}
    scaled = {c: s / temperature for c, s in sims.items()}
    max_scaled = max(scaled.values())
    exp_probs = {c: math.exp(v - max_scaled) for c, v in scaled.items()}
    total = math.fsum(exp_probs.values())
    if total <= 0.0:
        uniform = 1.0 / len(candidates)
        return {c: uniform for c in candidates}

    normalized = {c: v / total for c, v in exp_probs.items()}
    return _enforce_sum_to_one(normalized)


def normalize_json_probabilities(
    raw: Dict[str, float], candidates: List[str]
) -> Dict[str, float]:
    """Normalize VLM self-reported JSON probabilities.

    Missing candidates default to 0.0, values are clamped to [0, 1], and the
    clamped result is renormalized (uniform fallback when total is 0).
    """
    if not candidates:
        return {}

    probs: Dict[str, float] = {}
    total = 0.0
    for c in candidates:
        try:
            val = float(raw.get(c, 0.0))
        except (TypeError, ValueError):
            val = 0.0
        val = max(0.0, min(1.0, val))
        probs[c] = val
        total += val

    if total <= 0.0:
        uniform = 1.0 / len(candidates)
        return {c: uniform for c in candidates}

    normalized = {c: v / total for c, v in probs.items()}
    return _enforce_sum_to_one(normalized)


def apply_threshold(score_map: Dict[str, float], threshold: float) -> Dict[str, float]:
    """Inclusive threshold filter over a score map (0.0 disables filtering)."""
    if threshold is None or threshold <= 0.0:
        return dict(score_map)
    return {k: v for k, v in score_map.items() if v >= threshold}


def build_score_result(
    candidates: List[str],
    score_map: Dict[str, float],
    tier: SCORING_TIER,
    match_types: Optional[Dict[str, str]] = None,
    notes: Optional[List[str]] = None,
) -> ScoreResult:
    """Assemble a ScoreResult from a candidate list and a score map."""
    match_types = match_types or {}
    scored: List[ScoredKeyword] = []
    for candidate in candidates:
        match_type = match_types.get(candidate, "exact")
        scored.append(
            ScoredKeyword(
                keyword=candidate,
                score=float(score_map.get(candidate, 0.0)),
                matched=match_type != "none",
                match_type=match_type,
            )
        )
    return ScoreResult(
        scores=scored,
        tier=tier,
        calibrated=(tier == SCORING_TIER.LOGPROB),
        notes=list(notes or []),
    )


def build_thresholded_view(result: ScoreResult, threshold: float) -> ScoreResult:
    """Return a copy of ``result`` with sub-threshold entries filtered out."""
    kept_scores = apply_threshold(result.score_map, threshold)
    filtered = ScoreResult(
        scores=[s for s in result.scores if s.keyword in kept_scores],
        tier=result.tier,
        calibrated=result.calibrated,
        notes=list(result.notes)
        + [f"Threshold {threshold:.3f} applied; sub-threshold entries removed."],
    )
    return filtered


def unavailable_result(
    reason: str, candidates: Optional[List[str]] = None
) -> ScoreResult:
    """Build the explicit 'scoring could not run' result."""
    return ScoreResult(
        scores=[
            ScoredKeyword(keyword=c, score=0.0, matched=False, match_type="none")
            for c in (candidates or [])
        ],
        tier=SCORING_TIER.UNAVAILABLE,
        calibrated=False,
        notes=[reason],
    )


def _enforce_sum_to_one(values: Dict[str, float]) -> Dict[str, float]:
    """Final float-safety pass guaranteeing the sum-to-one invariant."""
    total = math.fsum(values.values())
    if total > 0.0 and abs(total - 1.0) > SUM_TO_ONE_TOLERANCE:
        values = {k: v / total for k, v in values.items()}
    return values
