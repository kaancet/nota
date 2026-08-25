"""stdio JSON-RPC boundary for a frontend (e.g. Avalonia). Run: `python -m nota.server`.

Thin glue: parse a JSON request -> call the headless core -> serialize the result.
No polars objects cross the wire (only preview payloads). No GUI, no logic of its own.

Protocol: JSON-lines. One request object per line in, one response per line out.
  request : {"id": <any>, "method": <str>, "params": {...}}
  response: {"id": <same>, "result": <json>}  |  {"id": <same>, "error": {"message": <str>}}

Methods:
  server_info  {}                       -> {protocol_version, methods}  (handshake first)
  get_manifest {tier?}                  -> node catalog for the palette
  run_graph    {graph, n?}              -> {node_id: preview payload | {error}}
  preview      {graph, node_id, n?}     -> one node's preview payload
  schema       {graph, node_id}         -> {col: dtype}  (no full run)
  validate     {graph}                  -> {ok, issues}  (cycles / missing ports / bad kinds)
"""

from __future__ import annotations

import json
import sys
from typing import Any

import nota.nodes  # noqa: F401  -- register builtin nodes before serving
from nota.core import Graph, manifest
from nota.core.preview import preview, schema_of
from nota.core.registry import REGISTRY

# Bump on any change to the message shapes (see PROTOCOL.md):
#   minor -> additive/backward-compatible (new optional field or method)
#   major -> breaking (renamed/removed field, changed meaning)
PROTOCOL_VERSION = "1.0"


def _run_capture(graph: Graph) -> tuple[dict, dict]:
    """Evaluate the graph, capturing per-node errors instead of aborting the whole run.
    Returns (cache, errors) keyed by node id; a failed node caches None."""
    cache: dict[str, Any] = {}
    errors: dict[str, dict] = {}
    for nid in graph.topo():  # raises on cycle -> caller turns it into an error response
        n = graph.nodes[nid]
        spec = REGISTRY.get(n.kind)
        if spec is None:
            errors[nid] = {"error": f"unknown kind {n.kind!r}"}
            cache[nid] = None
            continue
        try:
            inbound = {p: [cache[src] for src, _ in refs] for p, refs in n.inputs.items()}
            cache[nid] = spec.invoke(inbound, n.params)
        except Exception as e:  # noqa: BLE001 -- report, don't kill the session
            errors[nid] = {"error": f"{type(e).__name__}: {e}"}
            cache[nid] = None
    return cache, errors


def _server_info(params: dict) -> dict:
    """Handshake: the frontend calls this first to check protocol compatibility."""
    return {"protocol_version": PROTOCOL_VERSION, "methods": sorted(HANDLERS)}


def _get_manifest(params: dict) -> list[dict]:
    return manifest(params.get("tier", "common"))


def _run_graph(params: dict) -> dict:
    g = Graph.from_dict(params["graph"])
    n = params.get("n", 50)
    cache, errors = _run_capture(g)
    out = {}
    for nid in g.nodes:
        out[nid] = errors[nid] if nid in errors else preview(cache[nid], n)
    return out


def _preview(params: dict) -> dict:
    g = Graph.from_dict(params["graph"])
    node_id = params["node_id"]
    cache, errors = _run_capture(g)
    return errors.get(node_id) or preview(cache[node_id], params.get("n", 50))


def _schema(params: dict) -> dict:
    g = Graph.from_dict(params["graph"])
    node_id = params["node_id"]
    cache, errors = _run_capture(g)
    if node_id in errors:
        return errors[node_id]
    return schema_of(cache[node_id])


def _validate(params: dict) -> dict:
    g = Graph.from_dict(params["graph"])
    issues: list[dict] = []
    for nid, n in g.nodes.items():
        spec = REGISTRY.get(n.kind)
        if spec is None:
            issues.append({"node": nid, "issue": f"unknown kind {n.kind!r}"})
            continue
        for port in (p.name for p in spec.inputs if not p.optional):
            if not n.inputs.get(port):
                issues.append({"node": nid, "issue": f"missing required input {port!r}"})
    try:
        g.topo()
    except ValueError as e:
        issues.append({"issue": str(e)})
    return {"ok": not issues, "issues": issues}


HANDLERS = {
    "server_info": _server_info,
    "get_manifest": _get_manifest,
    "run_graph": _run_graph,
    "preview": _preview,
    "schema": _schema,
    "validate": _validate,
}


def handle(request: dict) -> dict:
    """Dispatch one request to a response. Pure -> unit-testable without a subprocess."""
    rid = request.get("id")
    fn = HANDLERS.get(request.get("method"))
    if fn is None:
        return {"id": rid, "error": {"message": f"unknown method {request.get('method')!r}"}}
    try:
        return {"id": rid, "result": fn(request.get("params") or {})}
    except Exception as e:  # noqa: BLE001 -- every error becomes a response, never a crash
        return {"id": rid, "error": {"message": f"{type(e).__name__}: {e}"}}


def main() -> None:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            resp = {"id": None, "error": {"message": f"bad json: {e}"}}
        else:
            resp = handle(req)
        sys.stdout.write(json.dumps(resp) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
