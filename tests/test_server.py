"""server.py RPC: drive handle() with request dicts, assert response dicts.
No subprocess needed -- the dispatch is pure."""

import json

from helpers import _test_source  # noqa: F401  (registers test.source)

from nota.core import Graph
from nota.server import handle


def _pipeline() -> dict:
    g = Graph().add("src", "test.source").add("pv", "sink.preview")
    g.connect("src", "pv", "frame")
    return g.to_dict()


def test_get_manifest():
    r = handle({"id": 1, "method": "get_manifest", "params": {"tier": "common"}})
    kinds = {e["kind"] for e in r["result"]}
    assert "sink.preview" in kinds and "LazyFrame.filter" in kinds
    json.dumps(r)                                   # response is wire-safe


def test_run_graph():
    r = handle({"id": 2, "method": "run_graph", "params": {"graph": _pipeline()}})
    assert r["result"]["pv"]["rows"] == [["a", 1], ["b", 2], ["a", 3]]


def test_schema():
    r = handle({"id": 3, "method": "schema",
                "params": {"graph": _pipeline(), "node_id": "src"}})
    assert r["result"] == {"g": "String", "v": "Int64"}


def test_validate_flags_missing_input():
    # filter with no wired 'self' -> validate reports it
    g = Graph().add("f", "LazyFrame.filter")
    r = handle({"id": 4, "method": "validate", "params": {"graph": g.to_dict()}})
    assert r["result"]["ok"] is False
    assert any("self" in i["issue"] for i in r["result"]["issues"])


def test_run_graph_captures_node_error():
    # Expr.sum receiving a frame instead of an expr -> that node errors, others don't crash
    g = Graph().add("src", "test.source").add("bad", "Expr.sum",
                                              inputs={"self": [("src", "out")]})
    r = handle({"id": 5, "method": "run_graph", "params": {"graph": g.to_dict()}})
    assert "error" in r["result"]["bad"]
    assert "type" in r["result"]["src"]             # upstream still produced a payload


def test_missing_required_input_message():
    # an unwired required port fails with a human message, not KeyError: 'self'
    g = Graph().add("f", "LazyFrame.filter")
    r = handle({"id": 20, "method": "run_graph", "params": {"graph": g.to_dict()}})
    assert "missing required input 'self' (frame)" in r["result"]["f"]["error"]


def test_missing_input_allows_literal_param():
    # a port-or-literal arg (Expr.gt.other) satisfied by a param, not a wire, is NOT flagged
    g = (Graph().add("col", "expr.column", {"name": "v"})
         .add("gt", "Expr.gt", {"other": 1}))
    g.connect("col", "gt", "self")
    r = handle({"id": 21, "method": "run_graph", "params": {"graph": g.to_dict()}})
    assert "error" not in r["result"]["gt"]           # param fallback, so no false positive


def test_node_log_and_timing():
    from nota.core.registry import node as reg_node

    @reg_node("test.talky", "Test")
    def _talky() -> int:
        import warnings as _w
        print("stdout line")  # noqa: T201
        _w.warn("a warning")
        return 1

    g = Graph().add("n", "test.talky").to_dict()
    r = handle({"id": 22, "method": "run_graph", "params": {"graph": g}})
    payload = r["result"]["n"]
    assert "stdout line" in payload["log"] and "a warning" in payload["log"]
    assert payload["ms"] >= 0


def test_columns_walks_to_the_frame():
    g = (Graph().add("src", "test.source").add("col", "expr.column", {"name": "v"})
         .add("gt", "Expr.gt", {"other": 1}).add("flt", "LazyFrame.filter"))
    g.connect("src", "flt", "self")
    g.connect("col", "gt", "self")
    g.connect("gt", "flt", "predicates")
    doc = g.to_dict()
    call = lambda nid: handle({"id": 1, "method": "columns", "params": {"graph": doc, "node_id": nid}})["result"]  # noqa: E731
    assert call("col") == ["g", "v"]                  # expr node walks downstream to filter's frame
    assert call("flt") == ["g", "v"]
    empty = handle({"id": 2, "method": "columns", "params": {"graph": {"nodes": []}, "node_id": "x"}})
    assert empty["result"] == []


def test_preview_prunes_to_ancestors():
    # an unrelated broken sibling must not pollute a preview of pv
    g = (Graph().add("src", "test.source").add("pv", "sink.preview")
         .add("bad", "source.csv", {"path": "/nonexistent_for_test.csv"}))
    g.connect("src", "pv", "frame")
    r = handle({"id": 23, "method": "preview", "params": {"graph": g.to_dict(), "node_id": "pv"}})
    assert r["result"]["rows"] == [["a", 1], ["b", 2], ["a", 3]]
    assert "ms" in r["result"]


def test_unknown_method():
    r = handle({"id": 6, "method": "nope", "params": {}})
    assert "error" in r and "unknown method" in r["error"]["message"]


def test_bad_params_becomes_error_not_crash():
    r = handle({"id": 7, "method": "run_graph", "params": {}})   # missing 'graph'
    assert "error" in r


# --- Plan A: serve() isolates stdout ---

def test_serve_noisy_node():
    """A node that print()s can't corrupt the JSON-lines response."""
    import io
    from nota.core.registry import node as reg_node

    @reg_node("test.noisy", "Test")
    def _noisy() -> int:
        print("stray output")  # noqa: T201
        return 42

    g = Graph().add("n", "test.noisy").to_dict()
    req = json.dumps({"id": 1, "method": "run_graph", "params": {"graph": g}}) + "\n"
    out = io.StringIO()
    import sys, nota.server  # noqa: E401
    saved = sys.stdout
    sys.stdout = sys.stderr  # mimic main()'s redirect
    try:
        nota.server.serve(io.StringIO(req), out)
    finally:
        sys.stdout = saved
    lines = [l for l in out.getvalue().splitlines() if l.strip()]
    assert len(lines) == 1
    resp = json.loads(lines[0])
    assert "result" in resp


def test_serve_bad_json():
    """Malformed JSON line → error envelope, not a crash."""
    import io
    from nota.server import serve
    out = io.StringIO()
    serve(io.StringIO("not json\n"), out)
    resp = json.loads(out.getvalue().strip())
    assert resp["id"] is None
    assert "bad json" in resp["error"]["message"]


# --- Plan B: upstream failure propagation ---

def test_upstream_failure_propagation():
    """Downstream of a failed node says 'upstream failed', not a misleading NoneType error."""
    g = (Graph()
         .add("bad", "source.csv", {"path": "/nonexistent_file_for_test.csv"})
         .add("h", "LazyFrame.head", inputs={"self": [("bad", "out")]})
         .add("pv", "sink.preview", inputs={"frame": [("h", "out")]}))
    r = handle({"id": 10, "method": "run_graph", "params": {"graph": g.to_dict()}})
    result = r["result"]
    assert "error" in result["bad"]
    assert "upstream" in result["h"]["error"]
    assert "upstream" in result["pv"]["error"]
    assert "type" not in result["pv"]  # no fake success payload
