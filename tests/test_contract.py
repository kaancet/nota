"""Layer A -- generic contract, auto-parametrized over every builtin node.
Adding a builtin sweeps it in here for free; no per-node work.
"""

import json

import pytest
from helpers import BUILTIN_KINDS

from nota.core import REGISTRY, manifest


@pytest.mark.parametrize("kind", sorted(BUILTIN_KINDS))
def test_builtin_spec_wellformed(kind):
    pub = REGISTRY[kind].public()
    json.dumps(pub)                                    # schema is JSON-safe
    ports = [i["name"] for i in pub["inputs"]]
    assert len(ports) == len(set(ports)), "duplicate port names"
    assert pub["outputs"], "node has no output port"


def test_manifest_json_safe_and_labeled():
    entries = manifest("all")
    json.dumps(entries)
    for e in entries:
        assert e["label"] and e["category"]
        assert e["tier"] in ("common", "advanced")
