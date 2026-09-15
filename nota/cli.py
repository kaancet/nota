"""Headless runner: `python -m nota run graph.nota`.

Loads a .nota (the frontend save format, or a raw backend graph doc), runs it through the
same `nota.server.handle` the GUI uses, and prints a per-node summary. Enables scripting,
cron, CI, and the examples regression test -- the payoff of a UI-independent pipeline.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def nota_to_graph_doc(doc: dict) -> dict:
    """Frontend .nota (nodes + edges) -> backend graph doc (edges as `inputs` on targets).

    Pass through unchanged if `doc` is already a graph doc (no top-level `edges`).
    """
    if "edges" not in doc:
        return doc
    nodes = {
        n["id"]: {"id": n["id"], "kind": n["kind"], "params": dict(n.get("params") or {}), "inputs": {}}
        for n in doc["nodes"]
    }
    for e in doc.get("edges", []):
        tgt = nodes.get(e["targetId"])
        if tgt is None:
            continue
        # variadic ports take many wires -> append, don't replace
        tgt["inputs"].setdefault(e["targetIn"], []).append([e["sourceId"], e.get("sourceOut", "out")])
    return {"nodes": list(nodes.values())}


def _summary(nid: str, kind: str, payload: dict) -> str:
    if "error" in payload:
        body = f"ERROR: {payload['error']}"
    else:
        t = payload.get("type")
        if t == "frame":
            r, c = (payload.get("shape") or [None, None])[:2]
            body = f"frame {r if r is not None else '?'}×{c}"
        elif t == "expr":
            body = f"expr {payload.get('repr', '')}"
        elif t == "scalar":
            body = f"scalar {payload.get('value')}"
        else:
            body = t or "?"
    ms = payload.get("ms")
    return f"{nid}  {kind}  → {body}" + (f"  ({ms} ms)" if ms is not None else "")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="nota")
    sub = ap.add_subparsers(dest="cmd", required=True)
    run = sub.add_parser("run", help="run a .nota pipeline")
    run.add_argument("file")
    run.add_argument("--n", type=int, default=50, help="preview row cap")
    run.add_argument("--node", default=None, help="run only this node and its ancestors")
    run.add_argument("--json", dest="as_json", action="store_true", help="dump the raw result")
    args = ap.parse_args(argv)

    import nota.nodes  # noqa: F401  -- register builtins
    from nota.core import load_user_imports, load_user_nodes
    from nota.server import handle

    load_user_imports()
    load_user_nodes()

    doc = nota_to_graph_doc(json.loads(Path(args.file).read_text(encoding="utf-8")))
    if args.node:
        resp = handle({"id": 1, "method": "preview", "params": {"graph": doc, "node_id": args.node, "n": args.n}})
        result = {args.node: resp["result"]} if "result" in resp else None
    else:
        resp = handle({"id": 1, "method": "run_graph", "params": {"graph": doc, "n": args.n}})
        result = resp.get("result")
    if result is None:
        print(f"error: {resp.get('error', {}).get('message')}", file=sys.stderr)
        return 1

    if args.as_json:
        print(json.dumps(result, indent=2))
    else:
        kinds = {n["id"]: n["kind"] for n in doc["nodes"]}
        for nid, payload in result.items():
            print(_summary(nid, kinds.get(nid, "?"), payload))
    return 1 if any(isinstance(p, dict) and "error" in p for p in result.values()) else 0
