"""Column selector nodes -- the schema-aware home for pl.col (which reflection can't
touch; see the pl.col override note). These are the leaves of most expr trees.

Schema-awareness lives at the frontend: it calls schema_of() on the upstream frame to
populate the `name`/`names` dropdowns. The nodes themselves just build the expr.
"""

import polars as pl
import polars.selectors as cs

from nota.core.registry import node


@node("expr.column", "Column")
def column(name: str) -> pl.Expr:
    """One column by name (frontend fills a dropdown from the frame schema)."""
    return pl.col(name)


@node("expr.columns", "Column")
def columns(names: list) -> pl.Expr:
    """Several columns by name (multi-select)."""
    return pl.col(*names)


@node("expr.col_regex", "Column")
def col_regex(pattern: str) -> pl.Expr:
    """Columns whose name matches a regex, e.g. '^value_'."""
    return cs.matches(pattern)


@node("expr.col_dtype", "Column")
def col_dtype(dtype: str) -> pl.Expr:
    """Columns of a given dtype, e.g. 'Int64', 'Float64', 'String'."""
    return cs.by_dtype(getattr(pl, dtype))
