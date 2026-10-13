"""Stats nodes -- summaries and correlations over a frame."""

from __future__ import annotations

import polars as pl

from nota.core.registry import node


@node("stats.describe", "Statistics")
def describe(frame: pl.LazyFrame) -> pl.LazyFrame:
    """Summary statistics (count, mean, std, min, quartiles, max) per column."""
    return frame.collect().describe().lazy()


@node("stats.profile", "Statistics")
def profile(frame: pl.LazyFrame) -> pl.LazyFrame:
    """One row per column: dtype, nulls, null %, distinct non-null values, min, max. First look at a new file."""
    df = frame.collect()
    n = df.height

    def _str(v):   # min/max of a list/struct column is undefined -> blank
        return None if v is None else str(v)

    rows = []
    for name, s in zip(df.columns, df.iter_columns()):
        simple = not isinstance(s.dtype, (pl.List, pl.Array, pl.Struct, pl.Object))
        nulls = s.null_count()
        rows.append({
            "column": name, "dtype": str(s.dtype), "rows": n, "nulls": nulls,
            "null_pct": round(100 * nulls / n, 1) if n else 0.0,
            "unique": s.drop_nulls().n_unique() if simple else None,
            "min": _str(s.min()) if simple else None, "max": _str(s.max()) if simple else None,
        })
    schema = {"column": pl.Utf8, "dtype": pl.Utf8, "rows": pl.Int64, "nulls": pl.Int64,
              "null_pct": pl.Float64, "unique": pl.Int64, "min": pl.Utf8, "max": pl.Utf8}
    return pl.DataFrame(rows, schema=schema).lazy()
