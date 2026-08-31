"""Literal nodes -- constants as expressions.

For feeding a constant into an *expr port* (arithmetic, when/then, comparisons wired
as nodes). Constants that go into a scalar *param* don't need a node -- just set the
param (e.g. Expr.gt other=4).
"""

import polars as pl

from nota.core.registry import node


@node("expr.lit", "Literal")
def lit(value: object) -> pl.Expr:
    """A constant expression. polars infers the dtype (int/float/str/bool)."""
    return pl.lit(value)


# typed constants -> the value param renders as a plain field of that type (Int64/Float64/String)
@node("expr.const_int", "Literal")
def const_int(value: int) -> pl.Expr:
    """An integer constant expression (Int64)."""
    return pl.lit(value)


@node("expr.const_float", "Literal")
def const_float(value: float) -> pl.Expr:
    """A float constant expression (Float64)."""
    return pl.lit(value)


@node("expr.const_str", "Literal")
def const_str(value: str) -> pl.Expr:
    """A string constant expression (String)."""
    return pl.lit(value)
