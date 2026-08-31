"""Sink nodes -- the terminals where a lazy pipeline finally runs.

preview is the most-used node in the tool: it materializes just enough to fill the
frontend grid. write_* stream to disk without a full in-memory collect.
"""

import polars as pl

from nota.core.preview import preview as _preview
from nota.core.registry import node


@node("sink.preview", "Sink")
def preview_sink(frame: object, *, n: int = 50) -> dict:
    """Preview any value in the output panel: a table for a frame/series, or the value/expr otherwise."""
    return _preview(frame, n)


@node("sink.write_csv", "Sink")
def write_csv(frame: pl.LazyFrame, *, path: str) -> str:
    """Stream a frame to a CSV file. Returns the written path."""
    frame.sink_csv(path)
    return path


@node("sink.write_parquet", "Sink")
def write_parquet(frame: pl.LazyFrame, *, path: str) -> str:
    """Stream a frame to a Parquet file. Returns the written path."""
    frame.sink_parquet(path)
    return path
