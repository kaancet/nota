"""Headless graph: plain-Python nodes + edges, topological run, JSON round-trip.

Knows nothing about DPG or any GUI. Values flowing on edges are whatever the
node kinds return -- LazyFrame, Expr, scalar. The graph doc IS the save file.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any

from .registry import REGISTRY


@dataclass
class GNode:
    id: str
    kind: str
    params: dict = field(default_factory=dict)
    # port name -> list of (src_node_id, src_out_port); list allows variadic ports
    inputs: dict[str, list[tuple[str, str]]] = field(default_factory=dict)


@dataclass
class Graph:
    nodes: dict[str, GNode] = field(default_factory=dict)

    def add(
        self, id: str, kind: str, params: dict | None = None, inputs: dict[str, list[tuple[str, str]]] | None = None
    ) -> Graph:
        self.nodes[id] = GNode(id, kind, params or {}, inputs or {})
        return self

    def connect(self, src: str, dst: str, port: str | None = None, out: str = "out") -> Graph:
        """Wire src.<out> -> dst.<port>, like dragging a link in the editor.

        port=None auto-picks when dst has exactly one input port. Variadic ports
        accumulate (connect again to add another wire); single ports get replaced.
        """
        for nid in (src, dst):
            if nid not in self.nodes:
                raise ValueError(f"no node {nid!r} in graph")
        spec = REGISTRY.get(self.nodes[dst].kind)
        ports = {p.name: p for p in spec.inputs} if spec else {}
        if port is None:
            if len(ports) != 1:
                raise ValueError(f"{dst!r} has ports {list(ports)}; name one")
            port = next(iter(ports))
        elif ports and port not in ports:
            raise ValueError(f"{dst!r} ({self.nodes[dst].kind}) has no port {port!r}; options: {list(ports)}")

        # type check: block a clear mismatch (e.g. Expr wired into a frame port).
        # "scalar" is our fuzzy fallback -> stay permissive, only block strict!=strict.
        src_spec = REGISTRY.get(self.nodes[src].kind)
        src_out = next((o for o in src_spec.outputs if o.name == out), None) if src_spec else None
        strict = {"expr", "frame", "series"}
        if src_out and ports.get(port):
            a, b = src_out.type, ports[port].type
            if a in strict and b in strict and a != b:
                raise ValueError(f"type mismatch: {src}.{out} is {a}, but {dst!r} port {port!r} wants {b}")

        refs = self.nodes[dst].inputs
        if ports and ports[port].variadic:
            refs.setdefault(port, []).append((src, out))
        else:
            refs[port] = [(src, out)]  # single input: newest wire wins
        return self

    def disconnect(self, src: str, dst: str, port: str | None = None, out: str = "out") -> Graph:
        """Remove the src.<out> -> dst.<port> wire, like deleting a link in the editor.

        port=None removes the wire from every port of dst. Idempotent: removing a
        wire that isn't there is a no-op. Empty ports are cleaned up.
        """
        if dst not in self.nodes:
            raise ValueError(f"no node {dst!r} in graph")
        refs = self.nodes[dst].inputs
        for p in [port] if port is not None else list(refs):
            if p in refs:
                refs[p] = [r for r in refs[p] if r != (src, out)]
                if not refs[p]:
                    del refs[p]
        return self

    def remove(self, id: str) -> Graph:
        """Delete a node and every wire touching it, like deleting a box in the editor.

        Its own inputs go with it; any downstream wire fed by it is stripped too.
        """
        if id not in self.nodes:
            raise ValueError(f"no node {id!r} in graph")
        del self.nodes[id]
        for n in self.nodes.values():
            for p in list(n.inputs):
                n.inputs[p] = [r for r in n.inputs[p] if r[0] != id]
                if not n.inputs[p]:
                    del n.inputs[p]
        return self

    def topo(self) -> list[str]:
        indeg = {nid: 0 for nid in self.nodes}
        children: dict[str, list[str]] = {nid: [] for nid in self.nodes}
        for nid, n in self.nodes.items():
            deps = {src for refs in n.inputs.values() for (src, _) in refs}
            indeg[nid] = len(deps)
            for src in deps:
                children[src].append(nid)
        queue = [nid for nid, d in indeg.items() if d == 0]
        order: list[str] = []
        while queue:
            nid = queue.pop()
            order.append(nid)
            for c in children[nid]:
                indeg[c] -= 1
                if indeg[c] == 0:
                    queue.append(c)
        if len(order) != len(self.nodes):
            raise ValueError("graph has a cycle")
        return order

    def to_dict(self) -> dict:
        return {
            "nodes": [
                {
                    "id": n.id,
                    "kind": n.kind,
                    "params": n.params,
                    "inputs": {p: [list(r) for r in refs] for p, refs in n.inputs.items()},
                }
                for n in self.nodes.values()
            ]
        }

    @classmethod
    def from_dict(cls, d: dict) -> Graph:
        g = cls()
        for n in d["nodes"]:
            g.add(
                n["id"],
                n["kind"],
                n.get("params", {}),
                {p: [tuple(r) for r in refs] for p, refs in n.get("inputs", {}).items()},
            )
        return g

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), indent=2)

    @classmethod
    def from_json(cls, s: str) -> Graph:
        return cls.from_dict(json.loads(s))


def run(graph: Graph) -> dict[str, Any]:
    """Evaluate every node once in topological order. Returns node_id -> output value."""
    cache: dict[str, Any] = {}
    for nid in graph.topo():
        n = graph.nodes[nid]
        spec = REGISTRY[n.kind]
        inbound = {port: [cache[src] for (src, _) in refs] for port, refs in n.inputs.items()}
        cache[nid] = spec.invoke(inbound, n.params)
    return cache
