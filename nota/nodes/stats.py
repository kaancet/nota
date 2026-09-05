"""Stats nodes -- summaries and correlations over a frame."""

from __future__ import annotations

import polars as pl

from nota.core.registry import node


@node("stats.describe", "Statistics")
def describe(frame: pl.LazyFrame) -> pl.LazyFrame:
    """Summary statistics (count, mean, std, min, quartiles, max) per column."""
    return frame.collect().describe().lazy()
