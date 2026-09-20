#!/usr/bin/env python3
"""Build guard for the inference sidecar.

The packaged sidecar runs LiquidAI LFM2.5-VL models, whose checkpoints ship no
``lm_head.weight``: the output head must be tied to the input embeddings at
load time. transformers 5.0.0 leaves ``tie_word_embeddings`` unset (defaulting
to False), so the head is randomly initialized and every ``/tag`` returns
gibberish. 5.1.0 fixed this by declaring ``tie_word_embeddings=True`` in the
LFM2-VL config; the model card also requires transformers >= 5.1.

This guard constructs the ``lfm2_vl`` config (no weights, no network) and fails
the sidecar build when the installed transformers would not tie the weights, so
a regression in the pin is caught at build time instead of shipping a broken
executable.

Usage: python build/check-lfm2vl-tie.py
Exit code 0 = OK, 1 = the installed transformers cannot tie LFM2.5-VL weights.
"""

from __future__ import annotations

import sys


def main() -> int:
    try:
        import transformers
        from transformers import AutoConfig
    except Exception as exc:  # pragma: no cover - import failure is the error
        print(f"FAIL: could not import transformers: {exc}")
        return 1

    try:
        config = AutoConfig.for_model("lfm2_vl")
    except Exception as exc:
        print(
            f"FAIL: transformers {transformers.__version__} has no usable "
            f"lfm2_vl config ({type(exc).__name__}: {exc})."
        )
        print("      Pin transformers>=5.1.0 in src/Synapic.Inference/requirements.txt.")
        return 1

    tied = getattr(config, "tie_word_embeddings", False)
    if tied is not True:
        print(
            f"FAIL: transformers {transformers.__version__} does not tie "
            f"LFM2.5-VL word embeddings (tie_word_embeddings={tied!r})."
        )
        print("      Every /tag would return gibberish; pin transformers>=5.1.0")
        print("      in src/Synapic.Inference/requirements.txt and rebuild.")
        return 1

    print(f"OK: transformers {transformers.__version__} ties LFM2.5-VL word embeddings.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
