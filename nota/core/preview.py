"""Turn any node output into a JSON-safe preview payload for the frontend grid.

This is the contract the table view consumes. The server calls preview(value) on any
cached node output -- click a node, see its data. Also the body of the sink.preview node.

Never runs a full query: LazyFrames use collect_schema() (schema without execution) and
head(n+1).collect() (fetch just enough to fill the grid + detect truncation).
"""

from __future__ import annotations

from typing import Any

import polars as pl


def _cell(v: Any) -> Any:
    """Coerce one cell to something json.dumps can handle."""
    if isinstance(v, (str, int, float, bool)) or v is None:
        return v
    if isinstance(v, (list, tuple)):
        return [_cell(x) for x in v]
    return str(v)  # datetime, Decimal, struct, nested, ...


def schema_of(value: Any) -> dict[str, str]:
    """Column -> dtype string, cheaply (no query execution). {} if not tabular."""
    if isinstance(value, pl.LazyFrame):
        s = value.collect_schema()
    elif isinstance(value, pl.DataFrame):
        s = value.schema
    elif isinstance(value, pl.Series):
        s = {value.name: value.dtype}
    else:
        return {}
    return {k: str(v) for k, v in s.items()}


def preview(value: Any, n: int = 50) -> dict:
    """JSON-safe snapshot of a node output. Shapes:
      frame  -> {type, columns, schema, rows, shape:[nrows|None, ncols], truncated}
      expr   -> {type, repr}
      scalar -> {type, value, dtype}
    """
    if isinstance(value, dict) and value.get("type") in {"frame", "expr", "scalar", "html"}:
        return value  # already a payload (e.g. sink.preview / sink.plot output) -> pass through
    if isinstance(value, pl.Expr):
        return {"type": "expr", "repr": str(value)}
    if isinstance(value, pl.Series):
        value = value.to_frame()

    if isinstance(value, pl.LazyFrame):
        schema = value.collect_schema()
        head = value.head(n + 1).collect()          # n+1 -> detect "more rows exist"
        return _frame_payload(head, n, dict(schema), nrows=None)
    if isinstance(value, pl.DataFrame):
        return _frame_payload(value.head(n + 1), n, dict(value.schema), nrows=value.height)

    return {"type": "scalar", "value": _cell(value), "dtype": type(value).__name__}


_MAX_COLS = 100   # cap columns in the payload -> a wide frame can't blow up the wire or the grid


def _frame_payload(head_df: pl.DataFrame, n: int, schema: dict, nrows: int | None) -> dict:
    truncated = head_df.height > n
    body = head_df.head(n)
    cols = list(schema.keys())
    shown = cols[:_MAX_COLS]                         # extra columns hidden; shape[1] keeps the true count
    if nrows is None and not truncated:
        nrows = body.height   # lazy but head fit entirely -> the count is exact, for free
    return {
        "type": "frame",
        "columns": shown,
        "schema": {k: str(v) for k, v in schema.items() if k in set(shown)},
        "rows": [[_cell(c) for c in row[:_MAX_COLS]] for row in body.rows()],
        "shape": [nrows, len(cols)],   # full [nrows|None, ncols]; ncols > len(columns) means columns were capped
        "truncated": truncated,
    }
