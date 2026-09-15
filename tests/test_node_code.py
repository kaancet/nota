"""Code nodes: SQL expression, SQL query, inline Python (+ print shows in the run log)."""

from helpers import covers, preview_of

from nota.core import Graph
from nota.server import handle


@covers("expr.sql")
def test_expr_sql():
    g = (Graph().add("src", "test.source").add("e", "expr.sql", {"expression": "v > 1"})
         .add("flt", "LazyFrame.filter").add("pv", "sink.preview"))
    g.connect("src", "flt", "self")
    g.connect("e", "flt", "predicates")
    g.connect("flt", "pv")
    assert preview_of(g)["rows"] == [["b", 2], ["a", 3]]


@covers("transform.sql")
def test_transform_sql():
    g = (Graph().add("src", "test.source")
         .add("q", "transform.sql", {"query": "SELECT g, SUM(v) AS s FROM self GROUP BY g ORDER BY g"})
         .add("pv", "sink.preview"))
    g.connect("src", "q", "frame")
    g.connect("q", "pv")
    out = preview_of(g)
    assert out["columns"] == ["g", "s"]
    assert out["rows"] == [["a", 4], ["b", 2]]


@covers("transform.python")
def test_transform_python():
    g = (Graph().add("src", "test.source")
         .add("py", "transform.python", {"code": "df = df.with_columns((pl.col('v') * 2).alias('w'))"})
         .add("pv", "sink.preview"))
    g.connect("src", "py", "frame")
    g.connect("py", "pv")
    assert "w" in preview_of(g)["columns"]


def test_transform_python_print_in_log():
    # ties to 1.1: a print() inside node code surfaces in the payload log
    g = (Graph().add("src", "test.source")
         .add("py", "transform.python", {"code": "print('dbg'); df = df"}))
    g.connect("src", "py", "frame")
    r = handle({"id": 1, "method": "run_graph", "params": {"graph": g.to_dict()}})
    assert "dbg" in r["result"]["py"]["log"]
