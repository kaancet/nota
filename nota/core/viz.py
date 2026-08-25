"""Dev-only graph visualization + structure helpers (optional networkx/matplotlib).

Not imported by nota.core -- keeps the core backend dependency-free. Install with:
    uv pip install -e ".[viz]"

The nota Graph stays the source of truth; to_nx() builds a throwaway view so the
whole networkx algorithm shelf (topo, descendants, cycles, components) is available
without restructuring anything.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import networkx as nx

    from .graph import Graph


def to_nx(graph: Graph) -> nx.MultiDiGraph:
    """Build a networkx MultiDiGraph view. Ports are kept as edge attributes;
    MultiDiGraph preserves parallel edges (e.g. two exprs into one variadic port)."""
    import networkx as nx

    g = nx.MultiDiGraph()
    for nid, n in graph.nodes.items():
        g.add_node(nid, kind=n.kind, params=n.params)
    for nid, n in graph.nodes.items():
        for in_port, refs in n.inputs.items():
            for src, out_port in refs:
                g.add_edge(src, nid, in_port=in_port, out_port=out_port)
    return g


def prunable(graph: Graph, keep: list[str]) -> set[str]:
    """Nodes that don't contribute to `keep` (your target sinks) -- safe to skip.
    = everything except the targets and their ancestors."""
    import networkx as nx

    g = to_nx(graph)
    needed = set(keep)
    for k in keep:
        needed |= nx.ancestors(g, k)
    return set(g) - needed


def cycles(graph: Graph) -> list[list[str]]:
    """Which nodes form a loop (empty if the graph is a valid DAG)."""
    import networkx as nx

    return list(nx.simple_cycles(to_nx(graph)))


def draw(graph: Graph, ax=None, figsize=(9, 5)):
    """Quick DAG picture for a notebook. Layered left->right by topological order.

    Returns the matplotlib Axes. Not for the product UI -- that's the frontend's job.
    """
    import matplotlib.pyplot as plt
    import networkx as nx

    g = to_nx(graph)
    try:  # clean layered layout by dependency depth
        for layer, gen in enumerate(nx.topological_generations(g)):
            for n in gen:
                g.nodes[n]["layer"] = layer
        pos = nx.multipartite_layout(g, subset_key="layer", align="vertical")
    except nx.NetworkXUnfeasible:  # has a cycle -> fall back, still draw it
        pos = nx.spring_layout(g, seed=0)

    if ax is None:
        _, ax = plt.subplots(figsize=figsize)
    labels = {n: f"{n}\n{g.nodes[n]['kind']}" for n in g}
    edge_labels = {(u, v): d["in_port"] for u, v, d in g.edges(data=True)}

    nx.draw_networkx_nodes(g, pos, ax=ax, node_color="#cfe3ff", node_size=2600)
    nx.draw_networkx_edges(
        g, pos, ax=ax, edge_color="#888", arrowsize=18, node_size=2600, connectionstyle="arc3,rad=0.05"
    )
    nx.draw_networkx_labels(g, pos, labels, ax=ax, font_size=8)
    nx.draw_networkx_edge_labels(g, pos, edge_labels, ax=ax, font_size=7, font_color="#c33")
    ax.set_axis_off()
    return ax


if __name__ == "__main__":  # smoke check: structure is correct (no display needed)
    from .graph import Graph

    g = Graph().add("a", "x").add("b", "y", inputs={"self": [("a", "out")]}).add("dead", "z")  # no path to a sink
    G = to_nx(g)
    assert G.number_of_nodes() == 3
    assert G.number_of_edges() == 1
    assert prunable(g, keep=["b"]) == {"dead"}  # keep b -> a needed, dead is not
    assert cycles(g) == []
    print("viz OK: nodes=3 edges=1 prunable(keep=b)={'dead'} cycles=[]")
