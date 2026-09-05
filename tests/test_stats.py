"""stats.describe (@node builtin) + the pingouin bridge (reflected, both call styles)."""

import polars as pl
from helpers import covers, preview_of

from nota.core import Graph, node, run


@covers("stats.describe")
def test_describe_summarizes():
    g = Graph().add("src", "test.source").add("d", "stats.describe").add("pv", "sink.preview")
    g.connect("src", "d", "frame")
    g.connect("d", "pv")
    out = preview_of(g)
    assert "statistic" in out["columns"]        # polars describe() -> a 'statistic' column per stat


# --- pingouin bridge: not builtins (fn lives in pingouin) so no @covers needed; prove both styles ---
@node("test.nums", "Test")
def _nums() -> pl.LazyFrame:
    return pl.LazyFrame({"x": [1.0, 2, 3, 4, 5, 6], "y": [2.0, 2, 4, 5, 7, 8]})


def _run(kind: str, params: dict) -> dict:
    g = Graph().add("s", "test.nums").add("n", kind, params).add("pv", "sink.preview")
    g.connect("s", "n", "data")
    g.connect("n", "pv")
    return run(g)["pv"]


def test_pingouin_array_style_mwu():
    out = _run("pg.mwu", {"x": "x", "y": "y"})   # x,y column names -> pdf[x], pdf[y] arrays
    assert "p_val" in out["columns"]


def test_pingouin_data_style_normality():
    out = _run("pg.normality", {})               # data=pdf -> per-column normality
    assert "W" in out["columns"] and "pval" in out["columns"]
