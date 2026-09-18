"""Tests for the ported keyword_scoring module (tier contract + softmax math).

Mirror of the original repo's unit tests for src/core/keyword_scoring.py —
the ported code must behave identically.
"""

import math
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "Synapic.Inference"))

from synapic_inference.keyword_scoring import (  # noqa: E402
    SCORING_TIER,
    ScoredKeyword,
    ScoreResult,
    apply_threshold,
    build_score_result,
    build_thresholded_view,
    normalize_json_probabilities,
    softmax_from_logprobs,
    softmax_from_similarities,
    unavailable_result,
)

SUM_TOL = 1e-9


def assert_sums_to_one(probs):
    total = math.fsum(probs.values())
    assert abs(total - 1.0) <= SUM_TOL


class TestSoftmaxFromLogprobs:
    def test_basic_distribution(self):
        probs = softmax_from_logprobs({"A": -0.12, "B": -2.9}, ["A", "B"])
        assert probs["A"] > probs["B"]
        assert_sums_to_one(probs)

    def test_missing_candidate_gets_zero(self):
        probs = softmax_from_logprobs({"A": -0.12}, ["A", "B"])
        assert probs["B"] == 0.0
        assert probs["A"] == 1.0
        assert_sums_to_one(probs)

    def test_all_missing_falls_back_to_uniform(self):
        probs = softmax_from_logprobs({}, ["A", "B", "C"])
        assert probs == {"A": 1 / 3, "B": 1 / 3, "C": 1 / 3}

    def test_numerical_stability_large_negative(self):
        probs = softmax_from_logprobs({"A": -1000.0, "B": -1001.0}, ["A", "B"])
        assert probs["A"] > probs["B"]
        assert_sums_to_one(probs)

    def test_empty_candidates(self):
        assert softmax_from_logprobs({"A": -1.0}, []) == {}


class TestSoftmaxFromSimilarities:
    def test_orders_by_similarity(self):
        sims = {"A": 0.31, "B": 0.22, "C": 0.28}
        probs = softmax_from_similarities(sims, ["A", "B", "C"], temperature=0.01)
        assert probs["A"] > probs["C"] > probs["B"]
        assert_sums_to_one(probs)

    def test_temperature_guard(self):
        sims = {"A": 0.3, "B": 0.2}
        probs = softmax_from_similarities(sims, ["A", "B"], temperature=0.0)
        assert_sums_to_one(probs)
        assert probs["A"] > probs["B"]

    def test_missing_similarity_defaults_to_minus_one(self):
        probs = softmax_from_similarities({"A": 0.3}, ["A", "B"])
        assert probs["B"] < probs["A"]
        assert_sums_to_one(probs)


class TestNormalizeJsonProbabilities:
    def test_clamps_and_normalizes(self):
        # Clamped values: A=1.0, B=0.0, C=0.3 → total 1.3 before renormalize.
        raw = {"A": 1.5, "B": -0.2, "C": 0.3}
        probs = normalize_json_probabilities(raw, ["A", "B", "C"])
        assert abs(probs["A"] - (1.0 / 1.3)) < 1e-9
        assert probs["B"] == 0.0
        assert abs(probs["C"] - (0.3 / 1.3)) < 1e-9
        assert_sums_to_one(probs)

    def test_total_zero_falls_back_to_uniform(self):
        probs = normalize_json_probabilities({}, ["A", "B"])
        assert probs == {"A": 0.5, "B": 0.5}

    def test_non_numeric_values_treated_as_zero(self):
        probs = normalize_json_probabilities({"A": "oops", "B": 0.5}, ["A", "B"])
        assert probs["A"] == 0.0
        assert probs["B"] == 1.0


class TestThresholding:
    def test_apply_threshold_inclusive(self):
        scores = {"A": 0.9, "B": 0.5, "C": 0.1}
        kept = apply_threshold(scores, 0.5)
        assert kept == {"A": 0.9, "B": 0.5}

    def test_apply_threshold_zero_disables(self):
        scores = {"A": 0.0, "B": 0.5}
        assert apply_threshold(scores, 0.0) == scores

    def test_build_thresholded_view_keeps_tier_and_notes(self):
        result = build_score_result(
            ["A", "B", "C"],
            {"A": 0.9, "B": 0.4, "C": 0.1},
            SCORING_TIER.EMBEDDING,
            notes=["origin note"],
        )
        view = build_thresholded_view(result, 0.5)
        assert [s.keyword for s in view.scores] == ["A"]
        assert view.tier == SCORING_TIER.EMBEDDING
        assert not view.calibrated
        assert "origin note" in view.notes
        assert any("Threshold" in n for n in view.notes)


class TestResultContract:
    def test_build_score_result_marks_unmatched(self):
        result = build_score_result(
            ["A", "B"],
            {"A": 0.7},
            SCORING_TIER.LABEL_CONFIDENCE,
            match_types={"A": "exact", "B": "none"},
        )
        assert result.scores[0].matched
        assert not result.scores[1].matched
        assert result.scores[1].score == 0.0
        assert not result.calibrated

    def test_logprob_tier_is_calibrated(self):
        result = build_score_result(["A"], {"A": 1.0}, SCORING_TIER.LOGPROB)
        assert result.calibrated

    def test_to_plain_dict_shape(self):
        result = ScoreResult(
            scores=[ScoredKeyword(keyword="A", score=0.5)],
            tier=SCORING_TIER.EMBEDDING,
            calibrated=False,
            notes=["n1"],
        )
        plain = result.to_plain_dict()
        assert plain["tier"] == "embedding"
        assert plain["calibrated"] is False
        assert plain["scores"][0]["keyword"] == "A"
        assert plain["notes"] == ["n1"]

    def test_unavailable_result_shape(self):
        result = unavailable_result("no provider", ["A", "B"])
        assert result.tier == SCORING_TIER.UNAVAILABLE
        assert not result.calibrated
        assert result.notes == ["no provider"]
        assert all(s.score == 0.0 and not s.matched for s in result.scores)

    def test_score_map_property(self):
        result = build_score_result(["A", "B"], {"A": 0.9, "B": 0.1}, SCORING_TIER.LOGPROB)
        assert result.score_map == {"A": 0.9, "B": 0.1}
