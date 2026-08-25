"""Shared test utilities. Importable from any test as `from helpers import ...`
(pytest puts the tests/ dir on sys.path).

BUILTIN_KINDS is auto-detected: every node registered from a nota.nodes module.
Add a builtin node -> it appears here -> the coverage guard demands a @covers test.
"""

import polars as pl
import pytest

import nota.nodes  # noqa: F401  -- registers the builtin nodes before we scan
from nota.core import REGISTRY, Graph, node, run

# builtin = hand-coded developer node (lives in nota.nodes); excludes reflected polars
# (module 'polars.*') and user/test nodes (module 'helpers', 'test_*').
BUILTIN_KINDS = frozenset(
    k for k, s in REGISTRY.items()
    if getattr(s.fn, "__module__", "").startswith("nota.nodes")
)


def covers(kind: str):
    """Mark a test as THE behavioral test for a builtin node `kind`.
    The coverage guard fails the suite if any builtin lacks one."""
    return pytest.mark.covers(kind)


def preview_of(g: Graph, sink: str = "pv"):
    """Run the graph, return the sink node's output payload."""
    return run(g)[sink]


# a reusable in-memory source for expr/column tests (module 'helpers' -> not a builtin)
@node("test.source", "Test")
def _test_source() -> pl.LazyFrame:
    return pl.LazyFrame({"g": ["a", "b", "a"], "v": [1, 2, 3]})
