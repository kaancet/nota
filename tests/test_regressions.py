"""Regression tests -- each encodes a real bug already hit, so it can't return."""

import polars as pl
import pytest
from helpers import preview_of

from nota.core import Graph


def test_keyword_only_param_passed_as_kwarg(tmp_path):
    # bug: invoke passed all params positionally -> keyword-only `path` crashed write_csv
    path = str(tmp_path / "o.csv")
    g = Graph().add("src", "test.source").add("wr", "sink.write_csv", {"path": path})
    g.connect("src", "wr", "frame")
    assert preview_of(g, "wr") == path


def test_preview_shape_exact_when_untruncated():
    # bug: lazy preview always reported shape rows = None even when the head fit
    from nota.core.preview import preview
    assert preview(pl.LazyFrame({"v": [1, 2, 3]}), n=50)["shape"] == [3, 1]


def test_type_mismatch_caught_at_connect():
    # bug: expr wired into a frame port failed cryptically at run; now blocked at connect
    g = Graph().add("cv", "pl.col", {"name": "v"}).add("flt", "LazyFrame.filter")
    with pytest.raises(ValueError, match="type mismatch"):
        g.connect("cv", "flt", "self")           # expr -> frame port


def test_and_predicates_via_variadic():
    # bug: understanding filter's variadic predicates as AND
    g = Graph().add("src", "test.source")
    g.add("c1", "pl.col", {"name": "v"}).add("gt", "Expr.gt", {"other": 1})
    g.add("c2", "pl.col", {"name": "v"}).add("lt", "Expr.lt", {"other": 3})
    g.add("flt", "LazyFrame.filter").add("pv", "sink.preview")
    g.connect("c1", "gt", "self")
    g.connect("c2", "lt", "self")
    g.connect("gt", "flt", "predicates")
    g.connect("lt", "flt", "predicates")         # both -> AND -> v==2
    g.connect("src", "flt", "self")
    g.connect("flt", "pv")
    assert preview_of(g)["rows"] == [["b", 2]]   # 1 < v < 3 -> v==2 (row g="b")
