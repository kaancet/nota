"""Contract guard. Pins the wire shapes documented in PROTOCOL.md.

Uses subset checks: adding a NEW field passes (additive/minor), but renaming or
removing a documented field FAILS here -- forcing a deliberate PROTOCOL.md edit and a
version bump before it can break the frontend. Keep in sync with PROTOCOL.md.
"""

import polars as pl
from helpers import _test_source  # noqa: F401  (registers test.source)

from nota.core import Graph, manifest
from nota.core.preview import preview
from nota.server import PROTOCOL_VERSION, handle

# documented required keys (PROTOCOL.md). additive fields allowed; these must persist.
NODE_SCHEMA_KEYS = {"kind", "category", "label", "tier", "doc", "inputs", "params", "outputs"}
PORT_KEYS = {"name", "type", "variadic", "optional"}
PARAM_KEYS = {"name", "type", "default", "required"}
FRAME_KEYS = {"type", "columns", "schema", "rows", "shape", "truncated"}


def _has(required: set, actual) -> bool:
    return required <= set(actual)


def test_protocol_version_handshake():
    r = handle({"id": 1, "method": "server_info"})
    assert r["result"]["protocol_version"] == PROTOCOL_VERSION
    assert "run_graph" in r["result"]["methods"]


def test_response_envelope_shape():
    ok = handle({"id": 9, "method": "server_info"})
    assert _has({"id", "result"}, ok) and ok["id"] == 9
    err = handle({"id": 9, "method": "nope"})
    assert _has({"id", "error"}, err) and _has({"message"}, err["error"])


def test_node_schema_shape():
    entry = next(e for e in manifest("common") if e["kind"] == "LazyFrame.filter")
    assert _has(NODE_SCHEMA_KEYS, entry)
    assert _has(PORT_KEYS, entry["inputs"][0])
    assert entry["inputs"][0]["type"] in {"frame", "expr", "series", "scalar", "any"}
    param = next(p for p in manifest("common")
                 if p["kind"] == "source.csv")["params"][0]
    assert _has(PARAM_KEYS, param)


def test_graph_document_shape():
    g = Graph().add("src", "test.source").add("pv", "sink.preview")
    g.connect("src", "pv", "frame")
    node = g.to_dict()["nodes"][0]
    assert _has({"id", "kind", "params", "inputs"}, node)
    pv = g.to_dict()["nodes"][1]
    assert pv["inputs"]["frame"] == [["src", "out"]]   # edge encoding


def test_preview_payload_shapes():
    lf = pl.LazyFrame({"g": ["a"], "v": [1]})
    assert _has(FRAME_KEYS, preview(lf))
    assert _has({"type", "repr"}, preview(pl.col("v").sum()))
    assert _has({"type", "value", "dtype"}, preview(1))


def test_run_graph_result_shape():
    g = Graph().add("src", "test.source").add("pv", "sink.preview")
    g.connect("src", "pv", "frame")
    r = handle({"id": 2, "method": "run_graph", "params": {"graph": g.to_dict()}})
    assert _has(FRAME_KEYS, r["result"]["pv"])
