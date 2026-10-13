"""stats.profile: one row per input column with dtype / nulls / unique / min / max."""

import polars as pl
from helpers import covers

from nota.core import Graph, node, run


@node("test.profile_src", "Test")
def _src() -> pl.LazyFrame:
    return pl.LazyFrame({"g": ["a", "a", "b", None], "v": [1, 2, 3, 3], "z": [[1], [2], [3], [4]]})


@covers("stats.profile")
def test_profile_per_column_stats():
    g = Graph().add("s", "test.profile_src").add("p", "stats.profile").add("pv", "sink.preview")
    g.connect("s", "p", "frame")
    g.connect("p", "pv")
    out = run(g)["pv"]
    rows = {r[out["columns"].index("column")]: dict(zip(out["columns"], r)) for r in out["rows"]}
    assert set(rows) == {"g", "v", "z"}
    assert rows["g"]["nulls"] == 1 and rows["g"]["null_pct"] == 25.0 and rows["g"]["unique"] == 2   # nulls not counted
    assert rows["v"]["min"] == "1" and rows["v"]["max"] == "3" and rows["v"]["unique"] == 3
    assert rows["z"]["unique"] is None and rows["z"]["min"] is None   # list column: no min/max/unique
