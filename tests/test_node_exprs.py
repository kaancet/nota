"""Behavioral tests for the column + literal builtin nodes."""

from helpers import covers, preview_of

from nota.core import Graph


def _select(colnode: str, params: dict) -> list:
    g = Graph().add("src", "test.source").add("c", colnode, params)
    g.add("sel", "LazyFrame.select").add("pv", "sink.preview")
    g.connect("src", "sel", "self")
    g.connect("c", "sel", "exprs")
    g.connect("sel", "pv")
    return preview_of(g)["columns"]


@covers("expr.column")
def test_column():
    assert _select("expr.column", {"name": "v"}) == ["v"]


@covers("expr.columns")
def test_columns():
    assert _select("expr.columns", {"names": ["g", "v"]}) == ["g", "v"]


@covers("expr.col_regex")
def test_col_regex():
    assert _select("expr.col_regex", {"pattern": "^v"}) == ["v"]


@covers("expr.col_dtype")
def test_col_dtype():
    assert _select("expr.col_dtype", {"dtype": "Int64"}) == ["v"]


@covers("expr.lit")
def test_lit():
    g = Graph().add("src", "test.source").add("k", "expr.lit", {"value": 7})
    g.add("al", "Expr.alias", {"name": "k"})
    g.add("wc", "LazyFrame.with_columns").add("pv", "sink.preview")
    g.connect("k", "al")
    g.connect("src", "wc", "self")
    g.connect("al", "wc", "exprs")
    g.connect("wc", "pv")
    out = preview_of(g)
    assert out["columns"] == ["g", "v", "k"]
    assert out["rows"][0] == ["a", 1, 7]
