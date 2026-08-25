"""viz structure helpers (optional dep -> skip if networkx absent)."""

import pytest

pytest.importorskip("networkx")

from nota.core import Graph  # noqa: E402
from nota.core.viz import cycles, prunable, to_nx  # noqa: E402


def test_to_nx_and_structure():
    g = (Graph()
         .add("a", "x")
         .add("b", "y", inputs={"self": [("a", "out")]})
         .add("dead", "z"))
    G = to_nx(g)
    assert G.number_of_nodes() == 3
    assert G.number_of_edges() == 1
    assert prunable(g, keep=["b"]) == {"dead"}
    assert cycles(g) == []
