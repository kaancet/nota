"""Behavioral tests for the sink.* builtin nodes."""

import polars as pl
from helpers import covers, preview_of

from nota.core import Graph


@covers("sink.preview")
def test_preview():
    g = Graph().add("src", "test.source").add("pv", "sink.preview")
    g.connect("src", "pv", "frame")
    out = preview_of(g)
    assert out["type"] == "frame"
    assert out["columns"] == ["g", "v"]
    assert out["rows"] == [["a", 1], ["b", 2], ["a", 3]]


@covers("sink.write_csv")
def test_write_csv(tmp_path):
    path = str(tmp_path / "out.csv")
    g = Graph().add("src", "test.source").add("wr", "sink.write_csv", {"path": path})
    g.connect("src", "wr", "frame")
    assert preview_of(g, "wr") == path
    assert pl.read_csv(path).rows() == [("a", 1), ("b", 2), ("a", 3)]


@covers("sink.write_parquet")
def test_write_parquet(tmp_path):
    path = str(tmp_path / "out.parquet")
    g = Graph().add("src", "test.source").add("wr", "sink.write_parquet", {"path": path})
    g.connect("src", "wr", "frame")
    assert preview_of(g, "wr") == path
    assert pl.read_parquet(path).rows() == [("a", 1), ("b", 2), ("a", 3)]
