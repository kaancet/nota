"""Composite (macro) nodes: a saved sub-graph registered as one runnable palette node."""

import nota.nodes  # noqa: F401  -- source.sample / sink.preview / LazyFrame.head
from nota.core import REGISTRY, Graph, create_node, load_user_nodes, manifest, run

# inner graph: io.input(frame) -> LazyFrame.head(n) -> io.output
# expose the frame as a wired port; promote head's `n` as a composite param (default 3).
_DEFN = {
    "name": "Top rows",
    "kind": "composite.top_rows",
    "inputs": [{"name": "frame", "type": "frame"}],
    "params": [{"name": "n", "type": "int", "default": 3}],
    "outputs": [{"name": "out", "type": "frame"}],
    "input_of": {"frame": "in"},
    "promoted": [["h", "n", "n", 3]],
    "output_node": "o",
    "subgraph": {"nodes": [
        {"id": "in", "kind": "io.input", "params": {}, "inputs": {}},
        {"id": "h", "kind": "LazyFrame.head", "params": {"n": 3}, "inputs": {"self": [["in", "out"]]}},
        {"id": "o", "kind": "io.output", "params": {}, "inputs": {"value": [["h", "out"]]}},
    ]},
}


def _outer(n: int | None) -> Graph:
    g = Graph().add("src", "source.sample")
    g.add("c", "composite.top_rows", {"n": n} if n is not None else {})
    g.add("pv", "sink.preview")
    g.connect("src", "c", "frame")
    g.connect("c", "pv")
    return g


def test_composite_runs_as_one_node(tmp_path):
    entry = create_node(_DEFN, tmp_path)
    assert entry["kind"] == "composite.top_rows"
    assert entry["category"] == "User generated"
    assert entry["inputs"][0]["type"] == "frame"
    assert entry["params"][0]["name"] == "n"

    assert run(_outer(2))["pv"]["shape"][0] == 2          # param override
    assert run(_outer(None))["pv"]["shape"][0] == 3       # promoted default (sample has 5 rows)


def test_composite_in_common_manifest(tmp_path):
    create_node(_DEFN, tmp_path)
    m = {e["kind"]: e for e in manifest("common")}
    assert m["composite.top_rows"]["category"] == "User generated"


def test_composite_persists_and_reloads(tmp_path):
    create_node(_DEFN, tmp_path)
    del REGISTRY["composite.top_rows"]                     # simulate a fresh process
    assert "composite.top_rows" in load_user_nodes(tmp_path)
    assert run(_outer(2))["pv"]["shape"][0] == 2


def test_build_rejects_self_reference_and_cycle(tmp_path):
    import pytest

    bad_self = {**_DEFN, "subgraph": {"nodes": [
        {"id": "x", "kind": "composite.top_rows", "params": {}, "inputs": {}},
    ]}, "output_node": "x"}
    with pytest.raises(ValueError):
        create_node(bad_self, tmp_path)
