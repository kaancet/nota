"""Every shipped example must run clean (no node errors). Skips until examples exist."""

import json
from pathlib import Path

import pytest

from nota.cli import nota_to_graph_doc
from nota.server import handle

_EXAMPLES = sorted((Path(__file__).parents[1] / "examples").glob("*.nota"))


@pytest.mark.skipif(not _EXAMPLES, reason="no examples/*.nota yet")
@pytest.mark.parametrize("path", _EXAMPLES, ids=lambda p: p.name)
def test_example_runs(path):
    doc = nota_to_graph_doc(json.loads(path.read_text(encoding="utf-8")))
    result = handle({"id": 1, "method": "run_graph", "params": {"graph": doc}})["result"]
    errored = {nid: p["error"] for nid, p in result.items() if isinstance(p, dict) and "error" in p}
    assert not errored, f"{path.name}: {errored}"
