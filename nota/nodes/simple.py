"""Simple nodes -- one self-contained step each: text / dropdown / checkbox params, no expression wiring.

Every node is a few polars calls. Column lists are comma-separated text ("a, b"); blank means
"all" or "any" where the docstring says so. The reflected LazyFrame / Expr nodes stay available
as the advanced tier.

No `from __future__ import annotations` here: the registry reads `Literal[...]` annotations to
build dropdowns, and string annotations would not resolve against this module's names.
"""

import re
from operator import eq, ge, gt, le, lt, ne
from typing import Literal

import polars as pl

from nota.core.registry import node

# ---- helpers ---------------------------------------------------------------


def _names(frame: pl.LazyFrame, text: str, what: str, *, blank_ok: bool = False) -> list[str]:
    """Split "a, b" into column names and check each exists (a typo gets the column list)."""
    names = [c.strip() for c in (text or "").split(",") if c.strip()]
    if not names and not blank_ok:
        raise ValueError(f"{what}: name at least one column")
    have = frame.collect_schema().names()
    bad = [n for n in names if n not in have]
    if bad:
        raise ValueError(f"{what}: no column named {', '.join(map(repr, bad))}. Columns: {', '.join(have)}")
    return names


_TRUE, _FALSE = {"true", "t", "yes", "y", "1"}, {"false", "f", "no", "n", "0"}


def _typed(value: str, dtype: pl.DataType, column: str, what: str):
    """Convert user text to a Python value of the column's dtype, or raise a plain-language error."""
    try:
        if dtype == pl.Boolean:
            v = value.strip().lower()
            if v in _TRUE | _FALSE:
                return v in _TRUE
            raise ValueError(v)
        return pl.Series([value]).cast(dtype).item()
    except Exception:  # noqa: BLE001 -- polars raises several types; all mean "doesn't fit"
        raise ValueError(f"{what}: {value!r} is not a valid {dtype} for column {column!r}") from None


def _sql(text: str, what: str, example: str) -> pl.Expr:
    if not (text or "").strip():
        raise ValueError(f"{what}: write something here, e.g. {example}")
    try:
        return pl.sql_expr(text)
    except Exception as e:  # noqa: BLE001
        raise ValueError(f"{what}: could not read {text!r} ({str(e).splitlines()[0]})") from None


# ---- Rows (filter / sort / clean) ------------------------------------------------------------------

_CMP = {"==": eq, "!=": ne, ">": gt, ">=": ge, "<": lt, "<=": le}


@node("simple.filter", "Basics")
def filter_rows(
    frame: pl.LazyFrame,
    *,
    column: str = "",
    operator: Literal["==", "!=", ">", ">=", "<", "<=", "contains", "is in", "is missing", "is not missing"] = "==",
    value: str = "",
    keep_missing: bool = False,
) -> pl.LazyFrame:
    """Keep rows where a column meets a condition. Chain two for AND; use Filter (expression) for OR.

    Parameters
    ----------
    column
        Column to test.
    operator
        How to compare. `is in` takes a comma-separated list in value.
    value
        What to compare with; converted to the column's type (numbers, dates, true/false).
    keep_missing
        Comparisons drop rows where the column is missing. Tick to keep those rows as well.
    """
    what = "Filter rows"
    (col,) = _names(frame, column, what)
    c = pl.col(col)
    if operator == "is missing":
        return frame.filter(c.is_null())
    if operator == "is not missing":
        return frame.filter(c.is_not_null())
    if operator == "contains":
        cond = c.cast(pl.Utf8).str.contains(value, literal=True)
    else:
        dtype = frame.collect_schema()[col]
        if operator == "is in":
            items = [_typed(v.strip(), dtype, col, what) for v in value.split(",")]
            cond = c.is_in(pl.Series(items, dtype=dtype).implode())
        else:
            cond = _CMP[operator](c, pl.lit(_typed(value, dtype, col, what), dtype=dtype))
    return frame.filter(cond | c.is_null() if keep_missing else cond)


@node("simple.filter_expression", "Basics")
def filter_expression(frame: pl.LazyFrame, *, condition: str = "") -> pl.LazyFrame:
    """Keep rows where a written condition is true. Handles OR and several columns at once.

    Parameters
    ----------
    condition
        SQL-style condition, e.g. ``score > 7 AND (group = 'A' OR group = 'B')``.
        Text in single quotes; column names with spaces in double quotes: ``"my col" > 3``.
    """
    return frame.filter(_sql(condition, "Filter (expression)", "score > 7 AND group = 'A'"))


@node("simple.sort", "Basics")
def sort_rows(frame: pl.LazyFrame, *, by: str = "") -> pl.LazyFrame:
    """Sort rows by one or more columns. Missing values go last.

    Parameters
    ----------
    by
        Columns in order, each optionally followed by ``desc`` (or ``asc``): ``score desc, age``.
    """
    names, desc = [], []
    for item in (by or "").split(","):
        parts = item.strip().rsplit(None, 1)
        if not parts:
            continue
        if len(parts) == 2 and parts[1].lower() in ("asc", "desc"):
            names.append(parts[0])
            desc.append(parts[1].lower() == "desc")
        else:
            names.append(item.strip())
            desc.append(False)
    _names(frame, ",".join(names), "Sort rows")
    return frame.sort(names, descending=desc, nulls_last=True, maintain_order=True)


@node("simple.drop_missing", "Basics")
def drop_missing(frame: pl.LazyFrame, *, columns: str = "") -> pl.LazyFrame:
    """Remove rows that have a missing value (empty cell or NaN).

    Parameters
    ----------
    columns
        Only look at these columns (comma-separated). Blank = a missing value in any column.
    """
    names = _names(frame, columns, "Remove missing", blank_ok=True)
    schema = frame.collect_schema()
    ok = []
    for n in names or schema.names():
        e = pl.col(n).is_not_null()
        ok.append(e & pl.col(n).is_not_nan() if schema[n].is_float() else e)
    return frame.filter(pl.all_horizontal(ok))


@node("simple.fill_missing", "Basics")
def fill_missing(
    frame: pl.LazyFrame,
    *,
    columns: str = "",
    how: Literal["value", "mean", "median", "min", "max", "zero", "forward", "backward"] = "value",
    value: str = "",
) -> pl.LazyFrame:
    """Replace missing values with a constant, a column statistic, or the neighbouring value.

    Parameters
    ----------
    columns
        Columns to fill (comma-separated). Blank = every column (numeric ones for mean / median / zero).
    how
        `value` uses the text in value; `forward` / `backward` copy the previous / next value.
    value
        The constant, converted to each column's type. Only used when how = value.
    """
    what = "Fill missing"
    names = _names(frame, columns, what, blank_ok=True)
    schema = frame.collect_schema()
    if not names:
        numeric_only = how in ("mean", "median", "zero")
        names = [n for n, d in schema.items() if d.is_numeric() or not numeric_only]
    exprs = []
    for n in names:
        c = pl.col(n)
        if how == "value":
            exprs.append(c.fill_null(pl.lit(_typed(value, schema[n], n, what), dtype=schema[n])))
        elif how in ("forward", "backward", "zero"):
            exprs.append(c.fill_null(strategy=how))
        else:
            exprs.append(c.fill_null(getattr(c, how)()))
    return frame.with_columns(exprs)


@node("simple.remove_duplicates", "Basics")
def remove_duplicates(
    frame: pl.LazyFrame, *, columns: str = "", keep: Literal["first", "last", "any", "none"] = "first"
) -> pl.LazyFrame:
    """Remove duplicate rows.

    Parameters
    ----------
    columns
        Rows count as duplicates when these columns match (comma-separated). Blank = the whole row.
    keep
        Which of a duplicate group to keep; `none` drops every row that has a duplicate.
    """
    names = _names(frame, columns, "Remove duplicates", blank_ok=True)
    return frame.unique(subset=names or None, keep=keep, maintain_order=True)


# ---- Columns ---------------------------------------------------------------


@node("simple.add_column", "Basics")
def add_column(frame: pl.LazyFrame, *, name: str = "", expression: str = "") -> pl.LazyFrame:
    """Add a column computed from others (or replace one with the same name).

    Parameters
    ----------
    name
        Name of the new column. An existing name is replaced.
    expression
        SQL-style expression, e.g. ``weight / (height * height)`` or
        ``CASE WHEN score > 7 THEN 'high' ELSE 'low' END``.
    """
    if not (name or "").strip():
        raise ValueError("Add column: give the new column a name")
    return frame.with_columns(_sql(expression, "Add column", "weight / (height * height)").alias(name.strip()))


@node("simple.keep_columns", "Basics")
def keep_columns(frame: pl.LazyFrame, *, columns: str = "") -> pl.LazyFrame:
    """Keep only these columns, in this order.

    Parameters
    ----------
    columns
        Comma-separated column names.
    """
    return frame.select(_names(frame, columns, "Keep columns"))


@node("simple.drop_columns", "Basics")
def drop_columns(frame: pl.LazyFrame, *, columns: str = "") -> pl.LazyFrame:
    """Remove these columns.

    Parameters
    ----------
    columns
        Comma-separated column names.
    """
    return frame.drop(_names(frame, columns, "Remove columns"))


@node("simple.rename_columns", "Basics")
def rename_columns(frame: pl.LazyFrame, *, mapping: str = "") -> pl.LazyFrame:
    """Rename columns.

    Parameters
    ----------
    mapping
        ``old = new`` pairs, comma-separated: ``subj = subject, rt_ms = reaction_time``.
    """
    pairs = {}
    for item in (mapping or "").split(","):
        if not item.strip():
            continue
        old, eq_, new = item.partition("=")
        if not eq_ or not old.strip() or not new.strip():
            raise ValueError(f"Rename columns: expected 'old = new', got {item.strip()!r}")
        pairs[old.strip()] = new.strip()
    if not pairs:
        raise ValueError("Rename columns: write at least one 'old = new' pair")
    _names(frame, ",".join(pairs), "Rename columns")
    return frame.rename(pairs)


_TYPES = {
    "integer": pl.Int64, "decimal": pl.Float64, "text": pl.Utf8, "boolean": pl.Boolean,
    "date": pl.Date, "datetime": pl.Datetime,
}


@node("simple.change_type", "Basics")
def change_type(
    frame: pl.LazyFrame,
    *,
    columns: str = "",
    to: Literal["integer", "decimal", "text", "boolean", "date", "datetime"] = "text",
    format: str = "",
    invalid: Literal["error", "make missing"] = "error",
) -> pl.LazyFrame:
    """Convert columns to another type.

    Parameters
    ----------
    columns
        Comma-separated column names.
    to
        Target type. Text to boolean reads true/false/yes/no/1/0; anything else becomes missing.
    format
        For text to date/datetime: a strptime pattern such as ``%d/%m/%Y``. Blank = detect.
    invalid
        What to do with values that cannot be converted: stop with an error, or make them missing.
    """
    names = _names(frame, columns, "Change type")
    schema = frame.collect_schema()
    strict = invalid == "error"
    exprs = []
    for n in names:
        c, is_text = pl.col(n), schema[n] == pl.Utf8
        if to == "boolean" and is_text:
            s = c.str.strip_chars().str.to_lowercase()
            e = pl.when(s.is_in(list(_TRUE))).then(True).when(s.is_in(list(_FALSE))).then(False).otherwise(None)
        elif to == "date" and is_text:
            e = c.str.to_date(format or None, strict=strict)
        elif to == "datetime" and is_text:
            e = c.str.to_datetime(format or None, strict=strict)
        else:
            e = c.cast(_TYPES[to], strict=strict)
        exprs.append(e.alias(n))
    return frame.with_columns(exprs)


# ---- Summarize -------------------------------------------------------------

_AGG = {
    "mean": lambda c: c.mean(),
    "median": lambda c: c.median(),
    "sum": lambda c: c.sum(),
    "min": lambda c: c.min(),
    "max": lambda c: c.max(),
    "first": lambda c: c.first(),
    "last": lambda c: c.last(),
    "sd": lambda c: c.std(),
    "var": lambda c: c.var(),
    "sem": lambda c: c.std() / c.count().sqrt(),
    "n_unique": lambda c: c.n_unique(),
    "missing": lambda c: c.null_count(),
    "count": lambda c: c.count(),
}
_METRIC = re.compile(r"^(\w+)\s*\(\s*(.*?)\s*\)\s*(?:as\s+(.+?))?$", re.IGNORECASE)


@node("simple.summarize", "Basics")
def summarize(frame: pl.LazyFrame, *, by: str = "", metrics: str = "n()") -> pl.LazyFrame:
    """One row per group with the statistics you list.

    Parameters
    ----------
    by
        Grouping columns (comma-separated). Blank = summarize the whole table into one row.
    metrics
        Comma-separated ``function(column)``, e.g. ``n(), mean(score), sd(score), sem(score)``.
        Output columns are named ``score_mean``; add ``as name`` to choose. Functions: n (rows),
        mean, median, sum, min, max, sd, var, sem, count (non-missing), n_unique, missing, first, last.
    """
    what = "Summarize"
    group = _names(frame, by, what, blank_ok=True)
    have = frame.collect_schema().names()
    exprs = []
    for item in (metrics or "").split(","):
        item = item.strip()
        if not item:
            continue
        m = _METRIC.match(item)
        if not m:
            raise ValueError(f"{what}: expected function(column), got {item!r}")
        fn, col, alias = m.group(1).lower(), m.group(2).strip("\"' "), m.group(3)
        if fn == "n":
            exprs.append(pl.len().alias(alias.strip() if alias else "n"))
            continue
        if fn not in _AGG:
            raise ValueError(f"{what}: unknown function {fn!r}. Use: n, {', '.join(_AGG)}")
        if col not in have:
            raise ValueError(f"{what}: no column named {col!r}. Columns: {', '.join(have)}")
        exprs.append(_AGG[fn](pl.col(col)).alias(alias.strip() if alias else f"{col}_{fn}"))
    if not exprs:
        raise ValueError(f"{what}: list at least one metric, e.g. n(), mean(score)")
    return frame.group_by(group, maintain_order=True).agg(exprs) if group else frame.select(exprs)


# ---- Reshape ---------------------------------------------------------------


@node("simple.wide_to_long", "Basics")
def wide_to_long(
    frame: pl.LazyFrame,
    *,
    id_columns: str = "",
    value_columns: str = "",
    variable_name: str = "variable",
    value_name: str = "value",
) -> pl.LazyFrame:
    """Stack several columns into two: which column it came from, and its value (wide -> long).

    Parameters
    ----------
    id_columns
        Columns that identify a row and stay as they are (comma-separated).
    value_columns
        Columns to stack. Blank = every column that is not an id column.
    variable_name
        Name of the new column holding the old column names.
    value_name
        Name of the new column holding the values.
    """
    ids = _names(frame, id_columns, "Wide to long", blank_ok=True)
    vals = _names(frame, value_columns, "Wide to long", blank_ok=True)
    return frame.unpivot(
        on=vals or None,
        index=ids or None,
        variable_name=variable_name.strip() or "variable",
        value_name=value_name.strip() or "value",
    )


@node("simple.long_to_wide", "Basics")
def long_to_wide(
    frame: pl.LazyFrame,
    *,
    index: str = "",
    names_from: str = "",
    values_from: str = "",
    aggregate: Literal["first", "sum", "mean", "median", "min", "max", "count"] = "first",
) -> pl.LazyFrame:
    """Spread one column's values into new columns (long -> wide).

    Parameters
    ----------
    index
        Columns that identify a row of the result (comma-separated). Blank = one row.
    names_from
        Column whose values become the new column names.
    values_from
        Column whose values fill the new columns.
    aggregate
        What to do when several rows land in the same cell.
    """
    what = "Long to wide"
    idx = _names(frame, index, what, blank_ok=True)
    (on,) = _names(frame, names_from, what)
    (vals,) = _names(frame, values_from, what)
    fn = "len" if aggregate == "count" else aggregate
    # pivot needs the data in memory (the output columns depend on the values)
    return frame.collect().pivot(on=on, index=idx or None, values=vals, aggregate_function=fn).lazy()


# ---- Combine ---------------------------------------------------------------


@node("simple.join", "Basics")
def join_tables(
    left: pl.LazyFrame,
    right: pl.LazyFrame,
    *,
    on: str = "",
    how: Literal["inner", "left", "full", "semi", "anti"] = "inner",
) -> pl.LazyFrame:
    """Combine two tables by matching key columns (same names in both).

    Parameters
    ----------
    on
        Key column(s) present in both tables (comma-separated). Rename first if the names differ.
    how
        `inner` keeps matches only; `left` keeps every left row; `full` keeps all rows of both;
        `semi` / `anti` keep left rows that do / do not have a match.
    """
    keys = _names(left, on, "Join")
    _names(right, on, "Join (right table)")
    return left.join(right, on=keys, how=how, coalesce=True)


@node("simple.append_rows", "Basics")
def append_rows(
    top: pl.LazyFrame, bottom: pl.LazyFrame, *, how: Literal["same columns", "any columns"] = "same columns"
) -> pl.LazyFrame:
    """Stack one table under another, e.g. sessions exported as separate files.

    Parameters
    ----------
    how
        `same columns` requires matching column names; `any columns` fills gaps with missing.
    """
    a, b = top.collect_schema().names(), bottom.collect_schema().names()
    if how == "same columns" and set(a) != set(b):
        raise ValueError(
            f"Stack tables: columns differ (only in top: {sorted(set(a) - set(b))}, "
            f"only in bottom: {sorted(set(b) - set(a))}). Choose 'any columns' to fill gaps with missing."
        )
    return pl.concat([top, bottom], how="vertical_relaxed" if how == "same columns" else "diagonal_relaxed")
