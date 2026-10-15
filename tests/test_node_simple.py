"""simple.* nodes: one self-contained step each. One @covers per node, plus a pipeline and error texts."""

import polars as pl
import pytest
from helpers import covers

from nota.core import Graph, node, run
from nota.server import handle


@node("test.simple_src", "Test")
def _src() -> pl.LazyFrame:
    return pl.LazyFrame(
        {
            "g": ["a", "b", "a", None, "b"],
            "s": [1.0, 5.0, 9.0, 7.0, None],
            "n": [1, 2, 3, 4, 5],
            "d": ["2024-01-01", "2024-03-01", "2024-06-01", "2024-09-01", None],
        }
    ).with_columns(pl.col("d").str.to_date())


@node("test.simple_right", "Test")
def _right() -> pl.LazyFrame:
    return pl.LazyFrame({"g": ["a", "b"], "label": ["Alpha", "Beta"]})


@node("test.simple_bottom", "Test")
def _bottom() -> pl.LazyFrame:
    return pl.LazyFrame({"g": ["z"], "s": [0.0], "n": [9], "extra": [True]})


def _run(kind, params=None, src="test.simple_src", port="frame"):
    g = Graph().add("s", src).add("n", kind, params or {}).add("pv", "sink.preview")
    g.connect("s", "n", port)
    g.connect("n", "pv")
    return run(g)["pv"]


def _rows(out):
    return out["rows"]


def _col(out, name):
    i = out["columns"].index(name)
    return [r[i] for r in out["rows"]]


# --- Rows -------------------------------------------------------------------


@covers("simple.filter")
def test_filter_operators_and_keep_missing():
    f = lambda **p: _col(_run("simple.filter", p), "n")  # noqa: E731
    assert f(column="s", operator=">", value="5") == [3, 4]                       # text -> number; null row dropped
    assert f(column="s", operator=">", value="5", keep_missing=True) == [3, 4, 5]   # checkbox keeps the missing row
    assert f(column="g", operator="==", value="a") == [1, 3]
    assert f(column="g", operator="contains", value="b") == [2, 5]
    assert f(column="g", operator="is in", value="a, b") == [1, 2, 3, 5]
    assert f(column="g", operator="is missing") == [4]
    assert f(column="g", operator="is not missing") == [1, 2, 3, 5]
    assert f(column="d", operator=">=", value="2024-06-01") == [3, 4]             # text -> date


@covers("simple.filter_expression")
def test_filter_expression_or():
    out = _run("simple.filter_expression", {"condition": "s > 8 OR g = 'b'"})
    assert _col(out, "n") == [2, 3, 5]


@covers("simple.sort")
def test_sort_desc_nulls_last():
    assert _col(_run("simple.sort", {"by": "s desc"}), "n") == [3, 4, 2, 1, 5]    # missing s last
    assert _col(_run("simple.sort", {"by": "g, n desc"}), "n") == [3, 1, 5, 2, 4]


@covers("simple.drop_missing")
def test_drop_missing_any_or_listed_columns():
    assert _col(_run("simple.drop_missing"), "n") == [1, 2, 3]                    # g null and s null rows gone
    assert _col(_run("simple.drop_missing", {"columns": "s"}), "n") == [1, 2, 3, 4]


def test_drop_missing_treats_nan_as_missing():
    @node("test.simple_nan", "Test")
    def nan_src() -> pl.LazyFrame:
        return pl.LazyFrame({"x": [1.0, float("nan"), 3.0]})

    assert _col(_run("simple.drop_missing", src="test.simple_nan"), "x") == [1.0, 3.0]


@covers("simple.fill_missing")
def test_fill_missing_modes():
    assert _col(_run("simple.fill_missing", {"columns": "s", "how": "value", "value": "0"}), "s") == [1, 5, 9, 7, 0]
    assert _col(_run("simple.fill_missing", {"columns": "g", "how": "value", "value": "?"}), "g")[3] == "?"
    assert _col(_run("simple.fill_missing", {"columns": "s", "how": "mean"}), "s")[4] == 5.5
    assert _col(_run("simple.fill_missing", {"columns": "s", "how": "forward"}), "s")[4] == 7.0
    assert _col(_run("simple.fill_missing", {"how": "zero"}), "s")[4] == 0.0       # blank = numeric columns


@covers("simple.remove_duplicates")
def test_remove_duplicates():
    assert _col(_run("simple.remove_duplicates", {"columns": "g"}), "n") == [1, 2, 4]
    assert _col(_run("simple.remove_duplicates", {"columns": "g", "keep": "last"}), "n") == [3, 4, 5]
    assert len(_rows(_run("simple.remove_duplicates"))) == 5                      # whole-row: all distinct


# --- Columns ----------------------------------------------------------------


@covers("simple.add_column")
def test_add_column():
    out = _run("simple.add_column", {"name": "n2", "expression": "n * 2"})
    assert _col(out, "n2") == [2, 4, 6, 8, 10]
    out = _run("simple.add_column", {"name": "n", "expression": "n + 100"})       # replace
    assert out["columns"] == ["g", "s", "n", "d"] and _col(out, "n")[0] == 101


@covers("simple.keep_columns")
def test_keep_columns_order():
    assert _run("simple.keep_columns", {"columns": "n, g"})["columns"] == ["n", "g"]


@covers("simple.drop_columns")
def test_drop_columns():
    assert _run("simple.drop_columns", {"columns": "s, d"})["columns"] == ["g", "n"]


@covers("simple.rename_columns")
def test_rename_columns():
    out = _run("simple.rename_columns", {"mapping": "g = group, n = trial"})
    assert out["columns"] == ["group", "s", "trial", "d"]


@covers("simple.change_type")
def test_change_type():
    assert _col(_run("simple.change_type", {"columns": "s", "to": "integer", "invalid": "make missing"}), "s")[:2] == [1, 5]
    assert _col(_run("simple.change_type", {"columns": "n", "to": "text"}), "n") == ["1", "2", "3", "4", "5"]
    assert _col(_run("simple.change_type", {"columns": "g", "to": "integer", "invalid": "make missing"}), "g") == [None] * 5
    with pytest.raises(Exception, match="(?i)conversion|strict|cast"):
        _run("simple.change_type", {"columns": "g", "to": "integer"})            # invalid=error is strict
    out = _run("simple.change_type", {"columns": "d", "to": "text"})
    assert _col(out, "d")[0] == "2024-01-01"


def test_change_type_text_to_date_with_format_and_boolean():
    @node("test.simple_txt", "Test")
    def txt() -> pl.LazyFrame:
        return pl.LazyFrame({"d": ["01/02/2024", "15/03/2024"], "b": ["Yes", "no"]})

    out = _run("simple.change_type", {"columns": "d", "to": "date", "format": "%d/%m/%Y"}, src="test.simple_txt")
    assert _col(out, "d") == ["2024-02-01", "2024-03-15"]
    out = _run("simple.change_type", {"columns": "b", "to": "boolean"}, src="test.simple_txt")
    assert _col(out, "b") == [True, False]


# --- Summarize / Reshape / Combine -----------------------------------------------


@covers("simple.summarize")
def test_summarize_by_group_and_whole_table():
    out = _run("simple.summarize", {"by": "g", "metrics": "n(), mean(s), sem(s) as s_sem, max(n)"})
    assert out["columns"] == ["g", "n", "s_mean", "s_sem", "n_max"]
    rows = {r[0]: r for r in _rows(out)}
    assert rows["a"][1] == 2 and rows["a"][2] == 5.0 and rows["b"][4] == 5
    out = _run("simple.summarize", {"metrics": "n(), missing(s), n_unique(g)"})
    assert out["columns"] == ["n", "s_missing", "g_n_unique"]
    assert _rows(out) == [[5, 1, 3]]                                              # n_unique counts null as a value


@covers("simple.wide_to_long")
def test_wide_to_long():
    out = _run("simple.wide_to_long", {"id_columns": "g", "value_columns": "s, n", "value_name": "val"})
    assert out["columns"] == ["g", "variable", "val"] and len(_rows(out)) == 10
    assert set(_col(out, "variable")) == {"s", "n"}


@covers("simple.long_to_wide")
def test_long_to_wide():
    @node("test.simple_long", "Test")
    def long() -> pl.LazyFrame:
        return pl.LazyFrame({"id": [1, 1, 2, 2], "cond": ["x", "y", "x", "y"], "v": [1, 2, 3, 4]})

    out = _run("simple.long_to_wide", {"index": "id", "names_from": "cond", "values_from": "v"}, src="test.simple_long")
    assert out["columns"] == ["id", "x", "y"] and _rows(out) == [[1, 1, 2], [2, 3, 4]]
    out = _run("simple.long_to_wide", {"index": "id", "names_from": "cond", "values_from": "v", "aggregate": "sum"},
               src="test.simple_long")
    assert _rows(out) == [[1, 1, 2], [2, 3, 4]]


@covers("simple.join")
def test_join():
    g = Graph().add("l", "test.simple_src").add("r", "test.simple_right").add("j", "simple.join", {"on": "g"})
    g.add("pv", "sink.preview")
    g.connect("l", "j", "left")
    g.connect("r", "j", "right")
    g.connect("j", "pv")
    out = run(g)["pv"]
    assert "label" in out["columns"] and len(out["rows"]) == 4                    # inner: the null key and no match drop
    g.nodes["j"].params["how"] = "left"
    assert len(run(g)["pv"]["rows"]) == 5


@covers("simple.append_rows")
def test_append_rows_modes():
    g = Graph().add("t", "test.simple_src").add("b", "test.simple_bottom").add("a", "simple.append_rows", {"how": "any columns"})
    g.add("pv", "sink.preview")
    g.connect("t", "a", "top")
    g.connect("b", "a", "bottom")
    g.connect("a", "pv")
    out = run(g)["pv"]
    assert len(out["rows"]) == 6 and "extra" in out["columns"]
    g.nodes["a"].params["how"] = "same columns"
    with pytest.raises(Exception, match="columns differ"):
        run(g)


# --- pipeline + friendly errors + manifest ------------------------------------------


def test_pipeline_filter_add_summarize():
    g = Graph().add("s", "test.simple_src")
    g.add("f", "simple.filter", {"column": "s", "operator": ">", "value": "0"})
    g.add("a", "simple.add_column", {"name": "big", "expression": "n > 2"})
    g.add("m", "simple.summarize", {"by": "big", "metrics": "n(), mean(s)"})
    g.add("pv", "sink.preview")
    g.connect("s", "f", "frame")
    g.connect("f", "a", "frame")
    g.connect("a", "m", "frame")
    g.connect("m", "pv")
    out = run(g)["pv"]
    assert out["columns"] == ["big", "n", "s_mean"]
    assert sorted(map(tuple, out["rows"])) == [(False, 2, 3.0), (True, 2, 8.0)]


@pytest.mark.parametrize(
    "kind, params, text",
    [
        ("simple.filter", {"column": "s", "operator": ">", "value": "abc"}, "'abc' is not a valid Float64 for column 's'"),
        ("simple.filter", {"column": "nope", "operator": "==", "value": "1"}, "no column named 'nope'"),
        ("simple.filter", {}, "name at least one column"),
        ("simple.filter_expression", {}, "write something here"),
        ("simple.add_column", {"expression": "n"}, "give the new column a name"),
        ("simple.summarize", {"metrics": "median_of(n)"}, "unknown function 'median_of'"),
        ("simple.summarize", {"metrics": "mean(nope)"}, "no column named 'nope'"),
        ("simple.rename_columns", {"mapping": "g"}, "expected 'old = new'"),
    ],
)
def test_friendly_errors(kind, params, text):
    g = Graph().add("s", "test.simple_src").add("n", kind, params).add("pv", "sink.preview")
    g.connect("s", "n", "frame")
    g.connect("n", "pv")
    resp = handle({"id": 1, "method": "run_graph", "params": {"graph": _doc(g)}})
    assert text in resp["result"]["n"]["error"]


def _doc(g):
    return {"nodes": [{"id": i, "kind": n.kind, "params": n.params, "inputs": n.inputs} for i, n in g.nodes.items()]}


def test_simple_nodes_are_common_and_replace_the_wiring_ones():
    from nota.core import catalog, manifest

    m = {e["kind"]: e for e in manifest("all")}
    for kind in [k for k in m if k.startswith("simple.")]:
        assert m[kind]["tier"] == "common" and m[kind]["category"] == "Basics"
    assert list(catalog("common"))[0] == "Basics"                                 # leads the palette
    assert m["LazyFrame.filter"]["tier"] == "advanced"                            # demoted, still runnable
    params = {p["name"]: p for p in m["simple.filter"]["params"]}
    assert params["value"]["widget"] == "text"                                    # no column autocomplete
    op = params["operator"]
    assert "contains" in op["choices"]                                            # Literal -> dropdown
