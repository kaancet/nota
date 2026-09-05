"""Composite (macro) nodes -- a saved sub-graph that acts as one node.

A composite is a second implementation of the same duck interface the engine speaks
(`invoke(inbound, params)` + `public()` + `.inputs/.outputs`), so it drops into REGISTRY
next to reflected polars and @node builtins and the engine treats it like any other kind
(run, preview, palette). Its `invoke` just runs the inner graph -- `core.run()` with two
tweaks: seed the boundary inputs, return the output node's value.

Boundary:
  io.input  -- one exposed input. Frame/expr/series -> a wired PORT; scalar -> a PARAM.
  io.output -- the exposed output (its value is the composite's output).
  promoted  -- an inner node's widget param lifted to a composite param (with a default).

Persistence: definitions are JSON in a user dir (default ~/.nota/nodes). load_user_nodes()
registers them at startup, so a composite is in the palette every session.
"""

from __future__ import annotations

import json
import logging
import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .graph import Graph
from .registry import REGISTRY, ParamSpec, PortSpec, register

loggr = logging.getLogger(__name__)


# --- io.* boundary placeholders (registered from core, so NOT counted as nota.nodes builtins) ---
def _io_input() -> object:  # value is injected at composite invoke time; never actually called
    return None


def _io_output(value: object) -> object:  # passthrough: its input is the composite's output
    return value


register(_io_input, "io.input", "IO", is_method=False)
register(_io_output, "io.output", "IO", is_method=False)


@dataclass
class CompositeSpec:
    kind: str
    name: str
    inputs: list[PortSpec]        # exposed wired ports (frame/expr/series)
    params: list[ParamSpec]       # exposed scalar inputs + promoted inner params
    outputs: list[PortSpec]
    subgraph: dict                # inner {nodes:[...]} -- a Graph doc
    input_of: dict[str, str]      # composite input/param name -> inner io.input node id
    promoted: list                # [inner_node_id, inner_param, composite_param, default]
    output_node: str              # inner node id whose value is returned
    definition: dict = field(default_factory=dict)   # the raw JSON, for persistence
    doc: str = ""
    is_method: bool = False       # engine never branches on this, but some code reads it

    def invoke(self, inbound: dict[str, list], params: dict) -> Any:
        g = Graph.from_dict(self.subgraph)   # fresh inner graph per call -> re-entrant
        cache: dict[str, Any] = {}

        # (a) seed exposed inputs: a wired value if connected, else the param / its default
        defaults = {p.name: p.default for p in self.params}
        for name, in_id in self.input_of.items():
            vals = inbound.get(name)
            cache[in_id] = vals[0] if vals else params.get(name, defaults.get(name))

        # (b) push promoted params onto their inner nodes
        for node_id, pname, cname, default in self.promoted:
            if node_id in g.nodes:
                g.nodes[node_id].params[pname] = params.get(cname, default)

        # (c) run the inner nodes -- identical to core.run(), skipping seeded boundaries
        for nid in g.topo():
            if nid in cache:
                continue
            n = g.nodes[nid]
            inb = {port: [cache[src] for (src, _) in refs] for port, refs in n.inputs.items()}
            cache[nid] = REGISTRY[n.kind].invoke(inb, n.params)

        return cache[self.output_node]

    def public(self) -> dict:
        return {
            "kind": self.kind,
            "category": "User generated",
            "label": self.name,
            "tier": "common",
            "doc": self.doc,
            "examples": "",
            "doc_url": None,
            "inputs": [vars(p) for p in self.inputs],
            "params": [vars(p) for p in self.params],
            "outputs": [vars(p) for p in self.outputs],
        }


def _slug(s: str) -> str:
    return re.sub(r"[^a-z0-9_]+", "_", s.strip().lower()).strip("_") or "node"


def build_composite(defn: dict) -> CompositeSpec:
    """Turn a definition dict into a CompositeSpec (validated, not yet registered)."""
    name = defn["name"]
    kind = defn.get("kind") or f"composite.{_slug(name)}"
    inputs = [PortSpec(i["name"], i["type"], doc=i.get("doc", "")) for i in defn.get("inputs", [])]
    params = [ParamSpec(p["name"], p.get("type", "any"), p.get("default"), p.get("required", False),
                        doc=p.get("doc", "")) for p in defn.get("params", [])]
    outputs = [PortSpec(o["name"], o["type"], doc=o.get("doc", "")) for o in defn.get("outputs", [])]
    subgraph = defn["subgraph"]
    output_node = defn["output_node"]

    g = Graph.from_dict(subgraph)          # raises on a malformed doc
    g.topo()                                # raises on a cycle
    if output_node not in g.nodes:
        raise ValueError(f"output_node {output_node!r} not in the sub-graph")
    for n in g.nodes.values():
        if n.kind == kind:
            raise ValueError(f"composite {kind!r} refers to itself")
        if n.kind not in REGISTRY:
            raise ValueError(f"sub-graph node {n.id!r} uses unknown kind {n.kind!r}")

    return CompositeSpec(
        kind=kind, name=name, inputs=inputs, params=params, outputs=outputs,
        subgraph=subgraph, input_of=defn.get("input_of", {}),
        promoted=[list(p) for p in defn.get("promoted", [])], output_node=output_node,
        definition={**defn, "kind": kind}, doc=defn.get("doc", ""),
    )


def user_nodes_dir() -> Path:
    return Path(os.environ.get("NOTA_USER_NODES") or Path.home() / ".nota" / "nodes")


def create_node(definition: dict, directory: str | Path | None = None) -> dict:
    """Register a composite from a definition + persist it to the user dir. Returns its
    manifest entry (same shape the palette consumes)."""
    spec = build_composite(definition)
    REGISTRY[spec.kind] = spec
    d = Path(directory) if directory else user_nodes_dir()
    d.mkdir(parents=True, exist_ok=True)
    (d / f"{spec.kind}.json").write_text(json.dumps(spec.definition, indent=2), encoding="utf-8")
    return spec.public()


def load_user_nodes(directory: str | Path | None = None) -> list[str]:
    """Register every persisted composite. Called at server startup. Returns loaded kinds."""
    d = Path(directory) if directory else user_nodes_dir()
    if not d.exists():
        return []
    loaded = []
    for f in sorted(d.glob("*.json")):
        try:
            spec = build_composite(json.loads(f.read_text(encoding="utf-8")))
            REGISTRY[spec.kind] = spec
            loaded.append(spec.kind)
        except Exception as e:  # noqa: BLE001 -- one bad file shouldn't sink startup
            loggr.warning("skip user node %s: %s", f.name, e)
    return loaded
