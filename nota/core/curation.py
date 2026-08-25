"""Curation layer: turn the ~979 raw registry kinds into a human palette.

Pure data + functions over the public() dicts. Affects only the *manifest* (what the
palette shows) -- every kind stays in REGISTRY and remains executable. Reflection gives
coverage; this gives usability.

Three jobs:
  hidden()   -- drop noise from the palette (eager families, converters, internals)
  COMMON     -- promote ~a few dozen everyday nodes with nice category + label
  decorate() -- stamp tier/category/label onto a manifest entry
"""

from __future__ import annotations

# substrings that mark a method as not-graph-useful (converters, escape hatches, internals)
_HIDE_SUBSTR = (
    "to_pandas", "to_numpy", "to_arrow", "to_torch", "to_jax", "to_dict", "to_init_repr",
    "map_elements", "map_batches", "map_groups", "serialize", "deserialize",
    "show_graph", "profile", "sink_", "write_",  # write_* covered by sink.* nodes
)


def hidden(kind: str) -> bool:
    """True if this kind should be omitted from the palette (still runnable)."""
    # this is a lazy-graph tool: hide the eager families, keep LazyFrame + Expr + the one groupby op
    if kind.startswith(("DataFrame.", "Series.", "GroupBy.")):
        return True
    if kind.startswith("LazyGroupBy.") and kind != "LazyGroupBy.agg":
        return True
    return any(s in kind for s in _HIDE_SUBSTR)


# promoted everyday nodes -> (category, label). Listing a kind that isn't registered is
# harmless (it just never matches), so this can be generous.
COMMON: dict[str, tuple[str, str]] = {
    # Source / Column / Literal
    "source.csv": ("Source", "Read CSV"), "source.parquet": ("Source", "Read Parquet"),
    "source.auto": ("Source", "Read File"),
    "expr.column": ("Column", "Column"), "expr.columns": ("Column", "Columns"),
    "expr.col_regex": ("Column", "Columns by regex"), "expr.col_dtype": ("Column", "Columns by type"),
    "expr.lit": ("Literal", "Constant"),
    # Transform (LazyFrame)
    "LazyFrame.filter": ("Transform", "Filter"), "LazyFrame.select": ("Transform", "Select"),
    "LazyFrame.with_columns": ("Transform", "With columns"), "LazyFrame.drop": ("Transform", "Drop"),
    "LazyFrame.rename": ("Transform", "Rename"), "LazyFrame.sort": ("Transform", "Sort"),
    "LazyFrame.unique": ("Transform", "Unique"), "LazyFrame.head": ("Transform", "Head"),
    "LazyFrame.drop_nulls": ("Transform", "Drop nulls"), "LazyFrame.fill_null": ("Transform", "Fill nulls"),
    "LazyFrame.join": ("Transform", "Join"), "LazyFrame.group_by": ("Transform", "Group by"),
    # Aggregate
    "LazyGroupBy.agg": ("Aggregate", "Aggregate"),
    "Expr.sum": ("Aggregate", "Sum"), "Expr.mean": ("Aggregate", "Mean"),
    "Expr.min": ("Aggregate", "Min"), "Expr.max": ("Aggregate", "Max"),
    "Expr.count": ("Aggregate", "Count"), "Expr.n_unique": ("Aggregate", "N unique"),
    "Expr.median": ("Aggregate", "Median"), "Expr.std": ("Aggregate", "Std"),
    "Expr.first": ("Aggregate", "First"), "Expr.last": ("Aggregate", "Last"),
    # Expression
    "Expr.alias": ("Expression", "Alias"), "Expr.cast": ("Expression", "Cast"),
    "Expr.round": ("Expression", "Round"), "Expr.abs": ("Expression", "Abs"),
    "Expr.fill_null": ("Expression", "Fill null"), "Expr.is_null": ("Expression", "Is null"),
    "Expr.over": ("Expression", "Over (window)"),
    # Compare / Logic
    "Expr.gt": ("Compare", ">"), "Expr.lt": ("Compare", "<"),
    "Expr.ge": ("Compare", ">="), "Expr.le": ("Compare", "<="),
    "Expr.eq": ("Compare", "=="), "Expr.ne": ("Compare", "!="),
    "Expr.and_": ("Logic", "AND"), "Expr.or_": ("Logic", "OR"), "Expr.not_": ("Logic", "NOT"),
    "Expr.is_in": ("Logic", "Is in"), "Expr.is_between": ("Logic", "Is between"),
    # String / Date / List
    "Expr.str.contains": ("String", "Contains"), "Expr.str.split": ("String", "Split"),
    "Expr.str.replace": ("String", "Replace"), "Expr.str.to_uppercase": ("String", "Uppercase"),
    "Expr.str.to_lowercase": ("String", "Lowercase"), "Expr.str.len_chars": ("String", "Length"),
    "Expr.str.strip_chars": ("String", "Strip"),
    "Expr.dt.year": ("Date", "Year"), "Expr.dt.month": ("Date", "Month"),
    "Expr.dt.day": ("Date", "Day"), "Expr.dt.weekday": ("Date", "Weekday"),
    "Expr.list.len": ("List", "List length"), "Expr.list.sum": ("List", "List sum"),
    "Expr.list.get": ("List", "List get"),
    # Sink
    "sink.preview": ("Sink", "Preview"), "sink.write_csv": ("Sink", "Write CSV"),
    "sink.write_parquet": ("Sink", "Write Parquet"),
}


def _default_category(kind: str) -> str:
    if kind.count(".") >= 2 and kind.startswith("Expr."):
        return "Expr." + kind.split(".")[1]      # namespace: Expr.str, Expr.dt, ...
    return kind.split(".")[0]                     # Expr, LazyFrame, pl, source, sink, ...


def decorate(entry: dict) -> dict | None:
    """Stamp tier/category/label onto a manifest entry; None if it should be hidden."""
    kind = entry["kind"]
    if hidden(kind):
        return None
    if kind in COMMON:
        entry["category"], entry["label"] = COMMON[kind]
        entry["tier"] = "common"
    else:
        entry["category"] = _default_category(kind)
        entry["label"] = kind.split(".")[-1].replace("_", " ")
        entry["tier"] = "advanced"
    return entry
