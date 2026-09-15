"""Code nodes -- write the expression instead of wiring it. SQL via polars' built-in
SQL engine; Python runs the user's own code (same trust model as import_node)."""
from __future__ import annotations

import polars as pl

from nota.core.registry import node


@node("expr.sql", "Expression")
def sql_expr(expression: str) -> pl.Expr:
    """An expression in SQL syntax, e.g. ``age > 18 AND country = 'TR'``.

    Parameters
    ----------
    expression
        SQL expression over column names. Wire the output into Filter / With columns / Select.
    """
    return pl.sql_expr(expression)


@node("transform.sql", "Transform")
def sql(frame: pl.LazyFrame, *, query: str = "SELECT * FROM self") -> pl.LazyFrame:
    """Query the input frame with SQL. The input table is named ``self``.

    Parameters
    ----------
    query
        e.g. ``SELECT country, AVG(income) AS avg_income FROM self GROUP BY country``
    """
    return pl.SQLContext(frames={"self": frame}).execute(query)


@node("transform.python", "Transform")
def python(frame: pl.LazyFrame, *, code: str = "df = df") -> pl.LazyFrame:
    """Run Python on the input. ``df`` is the input LazyFrame, ``pl`` is polars; leave the result in ``df``.

    Parameters
    ----------
    code
        e.g. ``df = df.with_columns((pl.col("income") / pl.col("size")).alias("per_head"))``
    """
    ns = {"pl": pl, "df": frame}
    exec(code, ns)  # noqa: S102 -- the user's own code on their own machine, like import_node
    out = ns["df"]
    return out.lazy() if isinstance(out, pl.DataFrame) else out
