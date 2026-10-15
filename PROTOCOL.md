# nota protocol

The contract between the Python backend (`nota.server`) and any frontend (Avalonia, …).
**This file is the source of truth.** The three JSON shapes below are the only coupling
between the two sides.

**Version: 1.7** (`nota.server.PROTOCOL_VERSION`)

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
| `columns` | `{graph, node_id}` | `[str]` — column names relevant to that node: only the frame(s) wired into it, or into the node that consumes it (expression nodes); `[]` when nothing is connected — **1.6** |
| `input_schema` | `{graph, node_id}` | `{port: {column: dtype_string}}` — the table(s) wired into that node, column order preserved; expression nodes use port `"*"` (the frame their consumer receives); `{}` when nothing is connected — **1.7** |
| `validate` | `{graph}` | `{ok: bool, issues: [{node?, issue}]}` |
| `import_node` | `{path}` | one **node schema** (a user `.py` with a single function, registered as category `Imported`) — **1.4** |
| `create_node` | `{definition}` | one **node schema** (a composite/macro node: a saved sub-graph registered as category `User generated`, persisted to the user dir) — **1.5** |

## Shape 1 — node schema (from `get_manifest`)

```json
{
  "kind": "LazyFrame.filter",
  "category": "Transform",
  "label": "Filter",
  "tier": "common",
  "doc": "Filter rows ...",
  "examples": ">>> lf.filter(pl.col(\"a\") > 1) ...",
  "doc_url": "https://docs.pola.rs/api/python/stable/reference/lazyframe/api/polars.LazyFrame.filter.html",
  "inputs":  [{"name": "self", "type": "frame", "variadic": false, "optional": false, "doc": ""}],
  "params":  [{"name": "n", "type": "int", "default": 50, "required": false, "doc": "How many ...", "choices": ["a", "b"]}],
  "outputs": [{"name": "out", "type": "frame", "variadic": false, "optional": false, "doc": ""}]
}
```
- `type`: `"frame" | "expr" | "series" | "scalar" | "any"`.
- **inputs** = wireable ports; `variadic` accepts many wires. **params** = widget values.
- **1.1 additions** (all optional): `examples` (str, may be `""`), `doc_url` (str or `null`),
  per-port/per-param `doc` (str, may be `""`), and `choices` (list of str) present only on enum params
  (reflected from a `Literal[...]` annotation) — the frontend renders a dropdown when it's present.
- **1.3**: an optional `widget` (str) on a param names a special frontend editor instead of a textbox.
- **1.7**: `widget` values `column`, `columns` and `sort` mark a param holding one column name / a comma list of column names / a comma list with optional `desc`|`asc` suffixes; the frontend renders chips.
  Values: `"kvlist"` (a key/value row editor, e.g. `dict.build`'s `entries`) and `"code"` (a multiline
  code editor, e.g. `transform.sql` / `transform.python` / `expr.sql`). Additive; omit for a plain textbox.
- **1.6**: an optional `aliases` (list of str) — search synonyms for the palette (e.g. `["distinct", "dedupe"]`
  on `LazyFrame.unique`). Present only when the kind has synonyms; the frontend matches them like the label.

## Shape 1b — composite definition (`create_node` param, **1.5**)

A macro node = a saved sub-graph exposed as one node. `create_node` registers it (category
`User generated`) and persists it to the user dir (`$NOTA_USER_NODES` or `~/.nota/nodes`), so it
reloads into the palette every session.

```json
{
  "name": "Top rows",
  "kind": "composite.top_rows",                              // optional; slugged from name if omitted
  "inputs":  [{"name": "frame", "type": "frame"}],           // exposed wired ports
  "params":  [{"name": "n", "type": "int", "default": 3}],   // exposed scalars + promoted inner params
  "outputs": [{"name": "out", "type": "frame"}],
  "subgraph": { "nodes": [ ... ] },                          // a Shape-2 graph doc (the inner nodes)
  "input_of": {"frame": "in"},                               // composite input/param name -> inner io.input node id
  "promoted": [["h", "n", "n", 3]],                          // [inner_node_id, inner_param, composite_param, default]
  "output_node": "o"                                         // inner node id whose value the composite returns
}
```
- Boundary kinds: `io.input` (0 inputs → its value is injected: a wire if connected, else the param/default)
  and `io.output` (passthrough — its input is the composite's output). Both live in the registry for the
  sub-editor to wire; they never appear in the `common` palette.
- v1 is single-output. Validation rejects a cycle, a missing `output_node`, an unknown inner kind, or self-reference.

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
{"type": "html", "html": "<!DOCTYPE html>..."}   // a plot node: self-contained interactive HTML (1.2+)
{"error": "ValueError: ..."}      // a node that failed
```
- **1.2**: a new preview type `html` (a `sink.plot.*` node returns a standalone HTML document with
  inlined JS). Additive — a frontend renders it in a webview; older ones ignore the unknown type.
- `shape`: `[nrows_or_null, ncols]` — `nrows` is `null` only when a lazy frame is truncated.
- **1.6**: every payload (including error payloads) also carries `ms` (float, always — the node's own
  compute time) and `log` (str, optional — the node's captured stdout + warnings, present only when
  non-empty). Honesty note: `ms` is the node's *own* compute. Lazy polars nodes only build a plan
  (≈0 ms) and are materialized together at preview time; eager nodes (stats, Python, plots, writes,
  imported) show real cost. A failing node's `log` is the most useful — it holds its prints.
- **1.6**: `preview` and `schema` evaluate only the target node's ancestors (a "run to here"), so an
  unrelated failing branch elsewhere in the graph does not affect them.
