# nota protocol

The contract between the Python backend (`nota.server`) and any frontend (Avalonia, …).
**This file is the source of truth.** The three JSON shapes below are the only coupling
between the two sides — change one and both sides must agree.

**Version: 1.0** (`nota.server.PROTOCOL_VERSION`)

## Versioning policy

- **minor bump** = additive, backward-compatible: a new optional field, or a new method.
  Old frontends keep working (they ignore unknown fields).
- **major bump** = breaking: a renamed/removed field, or changed meaning. Requires both
  sides to update.
- The frontend calls `server_info` first and refuses a **major** mismatch.
- `tests/test_protocol.py` guards these shapes — a breaking change fails there before it ships.

## Transport

JSON-lines over stdio (`python -m nota.server`). One request object per line in, one
response per line out. (WebSocket later reuses the same message bodies.)

```
request  : {"id": <any>, "method": <str>, "params": {...}}
response : {"id": <same>, "result": <json>} | {"id": <same>, "error": {"message": <str>}}
```

## Methods

| method | params | result |
|---|---|---|
| `server_info` | — | `{protocol_version, methods}` |
| `get_manifest` | `{tier?}` (`"common"`\|`"all"`, default `"common"`) | list of **node schema** |
| `run_graph` | `{graph, n?}` | `{node_id: preview \| {error}}` |
| `preview` | `{graph, node_id, n?}` | one **preview payload** |
| `schema` | `{graph, node_id}` | `{col: dtype_string}` |
| `validate` | `{graph}` | `{ok: bool, issues: [{node?, issue}]}` |

## Shape 1 — node schema (from `get_manifest`)

```json
{
  "kind": "LazyFrame.filter",
  "category": "Transform",
  "label": "Filter",
  "tier": "common",
  "doc": "Filter rows ...",
  "inputs":  [{"name": "self", "type": "frame", "variadic": false, "optional": false}],
  "params":  [{"name": "n", "type": "int", "default": 50, "required": false}],
  "outputs": [{"name": "out", "type": "frame", "variadic": false, "optional": false}]
}
```
- `type`: `"frame" | "expr" | "series" | "scalar" | "any"`.
- **inputs** = wireable ports; `variadic` accepts many wires. **params** = widget values.

## Shape 2 — graph document (frontend → backend; also the save file)

```json
{"nodes": [
  {"id": "src", "kind": "source.csv", "params": {"path": "x.csv"}, "inputs": {}},
  {"id": "pv",  "kind": "sink.preview", "params": {}, "inputs": {"frame": [["src", "out"]]}}
]}
```
- `inputs`: `{port_name: [[src_node_id, src_out_port], ...]}` — the edges live on the target.

## Shape 3 — preview payload (from `run_graph` / `preview`)

```json
{"type": "frame", "columns": ["g","v"], "schema": {"g":"String","v":"Int64"},
 "rows": [["a",1]], "shape": [3, 2], "truncated": false}
{"type": "expr", "repr": "col(\"v\").sum()"}
{"type": "scalar", "value": 42, "dtype": "int"}
{"error": "ValueError: ..."}      // a node that failed
```
- `shape`: `[nrows_or_null, ncols]` — `nrows` is `null` only when a lazy frame is truncated.
