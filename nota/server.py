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
  columns      {graph, node_id}         -> [str]  column names relevant to that node
  validate     {graph}                  -> {ok, issues}  (cycles / missing ports / bad kinds)
"""

from __future__ import annotations

import contextlib
import datetime
import io
import json
import logging
import sys
import time
import warnings
from pathlib import Path
from typing import Any

loggr = logging.getLogger("nota")

import nota.nodes  # noqa: F401  -- register builtin nodes before serving
from nota.core import Graph, create_node, import_function, load_user_nodes, manifest
import polars as pl

from nota.core.preview import frame_payload, preview, schema_of
from nota.core.registry import REGISTRY

# load_user_nodes() is called in main() so tests don't read ~/.nota

# Bump on any change to the message shapes (see PROTOCOL.md):
#   minor -> additive/backward-compatible (new optional field or method)
#   major -> breaking (renamed/removed field, changed meaning)
PROTOCOL_VERSION = "1.6"


def _missing_input(spec, n) -> Any:
    """The first required input port that is neither wired nor satisfiable by a literal
    param, else None. Catches an unwired `self` (a cryptic KeyError deep in invoke) while
    leaving port-or-literal args (which carry a same-named param fallback) alone."""
    pnames = {p.name for p in spec.params}
    return next(
        (p for p in spec.inputs
         if not p.optional and not p.variadic and p.name not in pnames and not n.inputs.get(p.name)),
        None,
    )


def _run_capture(graph: Graph) -> tuple[dict, dict, dict]:
    """Evaluate the graph, capturing per-node errors instead of aborting the whole run.
    Returns (cache, errors, meta) keyed by node id; a failed node caches None.
    meta[nid] = {"ms": float, "log": str?}: the node's own compute time, plus its captured
    stdout + warnings (only when non-empty)."""
    cache: dict[str, Any] = {}
    errors: dict[str, dict] = {}
    meta: dict[str, dict] = {}
    for nid in graph.topo():  # raises on cycle -> caller turns it into an error response
        n = graph.nodes[nid]
        # skip nodes whose upstream already failed
        failed_srcs = {src for refs in n.inputs.values() for (src, _) in refs if src in errors}
        if failed_srcs:
            errors[nid] = {"error": f"upstream node {sorted(failed_srcs)[0]!r} failed"}
            cache[nid] = None
            continue
        spec = REGISTRY.get(n.kind)
        if spec is None:
            errors[nid] = {"error": f"unknown kind {n.kind!r}"}
            cache[nid] = None
            continue
        miss = _missing_input(spec, n)
        if miss is not None:
            errors[nid] = {"error": f"missing required input {miss.name!r} ({miss.type})"}
            cache[nid] = None
            continue
        inbound = {p: [cache[src] for src, _ in refs] for p, refs in n.inputs.items()}
        buf, t0 = io.StringIO(), time.perf_counter()
        with contextlib.redirect_stdout(buf), warnings.catch_warnings(record=True) as w:
            warnings.simplefilter("always")
            try:
                cache[nid] = spec.invoke(inbound, n.params)
            except Exception as e:  # noqa: BLE001 -- report, don't kill the session
                loggr.warning("node %s (%s) failed: %s", nid, n.kind, e)
                errors[nid] = {"error": f"{type(e).__name__}: {e}"}
                cache[nid] = None
        log = buf.getvalue() + "".join(f"warning: {x.message}\n" for x in w)
        meta[nid] = {"ms": round((time.perf_counter() - t0) * 1000, 1), **({"log": log} if log else {})}
    return cache, errors, meta


def _server_info(params: dict) -> dict:
    """Handshake: the frontend calls this first to check protocol compatibility."""
    return {"protocol_version": PROTOCOL_VERSION, "methods": sorted(HANDLERS)}


def _get_manifest(params: dict) -> list[dict]:
    return manifest(params.get("tier", "common"))


def _run_graph(params: dict) -> dict:
    g = Graph.from_dict(params["graph"])
    n = params.get("n", 50)
    cache, errors, meta = _run_capture(g)
    out: dict = {}

    # Batch all lazy frames into one collect_all for shared sub-plan optimization
    lazies = [(nid, v) for nid, v in cache.items() if nid not in errors and isinstance(v, pl.LazyFrame)]
    if lazies:
        schemas = {nid: dict(v.collect_schema()) for nid, v in lazies}
        try:
            heads = pl.collect_all([v.head(n + 1) for _, v in lazies])
            for (nid, _), head in zip(lazies, heads):
                out[nid] = frame_payload(head, n, schemas[nid], nrows=None)
        except Exception:  # noqa: BLE001 -- fallback to per-node preview
            for nid, v in lazies:
                try:
                    out[nid] = preview(v, n)
                except Exception as e:  # noqa: BLE001
                    out[nid] = {"error": f"{type(e).__name__}: {e}"}

    for nid in g.nodes:
        if nid in out:
            continue
        out[nid] = errors[nid] if nid in errors else preview(cache[nid], n)
    for nid in g.nodes:  # per-node timing/log rides along on every payload (error payloads too)
        out[nid] = {**out[nid], **meta.get(nid, {})}
    return out


def _preview(params: dict) -> dict:
    node_id = params["node_id"]
    g = Graph.from_dict(params["graph"]).upto(node_id)  # run only what feeds this node
    cache, errors, meta = _run_capture(g)
    payload = errors.get(node_id) or preview(cache[node_id], params.get("n", 50))
    return {**payload, **meta.get(node_id, {})}


def _schema(params: dict) -> dict:
    node_id = params["node_id"]
    g = Graph.from_dict(params["graph"]).upto(node_id)
    cache, errors, _ = _run_capture(g)
    if node_id in errors:
        return errors[node_id]
    return schema_of(cache[node_id])


def _frame_sources(g: Graph, nid: str) -> list[str]:
    """Source node id(s) whose columns `nid` could refer to: the frame it feeds.

    An expr node (e.g. expr.column) has no frame input, so walk downstream to the first
    node with a wired frame port and take that port's source. Fall back to every source
    frame in the graph when nothing downstream consumes a frame yet.
    """
    def wired_frame_srcs(x: str) -> list[str]:
        n = g.nodes[x]
        spec = REGISTRY.get(n.kind)
        if spec is None:
            return []
        return [src for p in spec.inputs if p.type == "frame" for src, _ in n.inputs.get(p.name, [])]

    hit = wired_frame_srcs(nid)
    if hit:
        return hit
    # ponytail: BFS to the first frame consumer; ambiguity (two frames) -> union. Good enough until someone complains.
    children: dict[str, list[str]] = {k: [] for k in g.nodes}
    for k, n in g.nodes.items():
        for refs in n.inputs.values():
            for src, _ in refs:
                children[src].append(k)
    seen, queue = {nid}, list(children[nid])
    while queue:
        x = queue.pop(0)
        if x in seen:
            continue
        seen.add(x)
        hit = wired_frame_srcs(x)
        if hit:
            return hit
        queue += children[x]
    return [k for k, n in g.nodes.items()
            if not n.inputs and (s := REGISTRY.get(n.kind)) and s.outputs and s.outputs[0].type == "frame"]


def _columns(params: dict) -> list[str]:
    """Column names relevant to a node -- feeds the frontend's column-name dropdown."""
    g = Graph.from_dict(params["graph"])
    nid = params["node_id"]
    if nid not in g.nodes:
        return []
    cols: set[str] = set()
    for src in _frame_sources(g, nid):
        cache, errors, _ = _run_capture(g.upto(src))
        if src not in errors and cache.get(src) is not None:
            cols |= set(schema_of(cache[src]).keys())
    return sorted(cols)


def _import_node(params: dict) -> dict:
    """Register a user .py function as an 'Imported' node; returns its manifest entry."""
    return import_function(params["path"])


def _create_node(params: dict) -> dict:
    """Register + persist a composite (macro) node from its definition; returns its manifest entry."""
    return create_node(params["definition"])


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
    "columns": _columns,
    "validate": _validate,
    "import_node": _import_node,
    "create_node": _create_node,
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
        loggr.exception("method %r failed", request.get("method"))
        return {"id": rid, "error": {"message": f"{type(e).__name__}: {e}"}}


_LOG_KEEP_DAYS = 7


def _setup_logging() -> Path:
    """One log file per session under ~/.nota/logs, named by start time -> errors are traceable."""
    d = Path.home() / ".nota" / "logs"
    d.mkdir(parents=True, exist_ok=True)
    # prune logs older than _LOG_KEEP_DAYS
    cutoff = time.time() - _LOG_KEEP_DAYS * 86400
    for f in d.glob("nota-*.log"):
        try:
            if f.stat().st_mtime < cutoff:
                f.unlink()
        except OSError:
            pass
    path = d / f"nota-{datetime.datetime.now():%Y%m%d-%H%M%S}.log"
    logging.basicConfig(
        filename=str(path), level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    loggr.info("session start (protocol %s) -> %s", PROTOCOL_VERSION, path)
    return path


def serve(inp, out) -> None:
    """Read JSON-lines from *inp*, write responses to *out*. Testable with StringIO."""
    for line in inp:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            loggr.warning("bad json: %s", e)
            resp = {"id": None, "error": {"message": f"bad json: {e}"}}
        else:
            resp = handle(req)
        out.write(json.dumps(resp) + "\n")
        out.flush()


def main() -> None:
    _setup_logging()
    load_user_nodes()
    # Protect the RPC channel: node code printing to stdout can't corrupt responses.
    real_out = sys.stdout
    sys.stdout = sys.stderr
    serve(sys.stdin, real_out)


if __name__ == "__main__":
    main()
