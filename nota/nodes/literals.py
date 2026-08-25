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
