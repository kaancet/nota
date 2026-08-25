"""Graph engine: topo, run/memoization, connect/disconnect/remove, JSON round-trip."""

import polars as pl
import pytest

from nota.core import Graph, node, run


@node("test.counter", "Test")
def _counter() -> pl.LazyFrame:
    _counter.calls += 1
    return pl.LazyFrame({"x": [1]})
_counter.calls = 0


def test_topo_and_cycle():
    g = Graph().add("a", "x").add("b", "y", inputs={"self": [("a", "out")]})
    assert g.topo().index("a") < g.topo().index("b")
    g.nodes["a"].inputs = {"z": [("b", "out")]}          # make a cycle
    with pytest.raises(ValueError, match="cycle"):
        g.topo()


def test_diamond_runs_each_node_once():
    _counter.calls = 0
    # a feeds two downstream nodes; `a` must evaluate exactly once (memoized)
    g = Graph().add("a", "test.counter")
    g.add("b", "LazyFrame.head", inputs={"self": [("a", "out")]})
    g.add("c", "LazyFrame.head", inputs={"self": [("a", "out")]})
    run(g)
    assert _counter.calls == 1


def test_connect_variadic_accumulates():
    g = Graph().add("a", "pl.col", {"name": "x"}).add("b", "pl.col", {"name": "y"})
    g.add("sel", "LazyFrame.select")
    g.connect("a", "sel", "exprs")
    g.connect("b", "sel", "exprs")
    assert g.nodes["sel"].inputs["exprs"] == [("a", "out"), ("b", "out")]


def test_connect_single_port_replaces():
    g = Graph().add("a", "pl.col", {"name": "x"}).add("b", "pl.col", {"name": "y"})
    g.add("sum", "Expr.sum")
    g.connect("a", "sum", "self")
    g.connect("b", "sum", "self")                         # single port: newest wins
    assert g.nodes["sum"].inputs["self"] == [("b", "out")]


def test_connect_errors():
    g = Graph().add("a", "pl.col", {"name": "x"}).add("f", "LazyFrame.filter")
    with pytest.raises(ValueError, match="name one"):
        g.connect("a", "f")                               # ambiguous port
    with pytest.raises(ValueError, match="no port"):
        g.connect("a", "f", "nope")
    with pytest.raises(ValueError, match="no node"):
        g.connect("missing", "f", "self")


def test_disconnect_and_remove():
    g = Graph().add("a", "pl.col", {"name": "x"}).add("b", "pl.col", {"name": "y"})
    g.add("sel", "LazyFrame.select")
    g.connect("a", "sel", "exprs")
    g.connect("b", "sel", "exprs")
    g.disconnect("a", "sel", "exprs")
    assert g.nodes["sel"].inputs["exprs"] == [("b", "out")]
    g.disconnect("b", "sel", "exprs")
    assert "exprs" not in g.nodes["sel"].inputs           # emptied port cleaned
    g.disconnect("b", "sel", "exprs")                     # idempotent, no raise
    g.connect("a", "sel", "exprs")
    g.remove("a")
    assert "a" not in g.nodes
    assert "exprs" not in g.nodes["sel"].inputs           # dangling wire stripped


def test_json_round_trip():
    import nota.nodes  # noqa: F401
    g = Graph().add("src", "test.source").add("pv", "sink.preview")
    g.connect("src", "pv", "frame")
    reloaded = Graph.from_json(g.to_json())
    assert run(reloaded)["pv"]["rows"] == run(g)["pv"]["rows"]
