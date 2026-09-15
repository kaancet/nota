"""Plot nodes — each takes a frame + channels and returns a (fig, ax) figure tuple.

Wire the optional ``figure`` input to overlay multiple plots on the same axes.
``preview()`` converts the tuple to standalone HTML when the frontend requests it.
"""

from __future__ import annotations

from typing import NewType

import polars as pl

from nota.core.registry import node

Figure = NewType("Figure", object)

_bv = None


def _load_viz():
    global _bv
    if _bv is None:
        import behaviz as bv
        bv.set_renderer("bokeh")
        _bv = bv

_ROWCAP = 10_000


def _collect(frame: pl.LazyFrame) -> pl.DataFrame:
    if isinstance(frame, pl.LazyFrame):
        return frame.head(_ROWCAP).collect()
    if isinstance(frame, pl.DataFrame):
        return frame.head(_ROWCAP)
    return frame


def _render(plot_name: str, frame: pl.LazyFrame, overrides: dict, figure: object = None, *, preset: str, **channels) -> tuple:
    """Run a behaviz plot fn, return (fig, ax) for downstream overlay."""
    _load_viz()
    fn = getattr(_bv, plot_name)
    spec = _bv.load_preset(preset) if preset else None
    ax = figure[1] if isinstance(figure, tuple) and len(figure) == 2 else None
    return fn(data=_collect(frame), ax=ax, spec=spec, **channels, **(overrides or {}))


@node("sink.plot.line", "Plot")
def plot_line(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, preset: str = "") -> Figure:
    """Line plot. `x`/`y` are column names; wire a dict into `overrides` for any bokeh styling kwarg."""
    return _render("plot_line", frame, overrides, figure, x=x, y=y, preset=preset)


@node("sink.plot.scatter", "Plot")
def plot_scatter(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, preset: str = "") -> Figure:
    """Scatter plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render("plot_scatter", frame, overrides, figure, x=x, y=y, preset=preset)


@node("sink.plot.bar", "Plot")
def plot_bar(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, preset: str = "") -> Figure:
    """Bar plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render("plot_bar", frame, overrides, figure, x=x, y=y, preset=preset)


@node("sink.plot.step", "Plot")
def plot_step(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, preset: str = "") -> Figure:
    """Step plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render("plot_step", frame, overrides, figure, x=x, y=y, preset=preset)


@node("sink.plot.hbar", "Plot")
def plot_hbar(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, y: str, x: str, preset: str = "") -> Figure:
    """Horizontal bar plot. `y` is the category column, `x` the value column; `overrides` forwards any bokeh styling kwarg."""
    return _render("plot_hbar", frame, overrides, figure, y=y, x=x, preset=preset)


@node("sink.plot.hist", "Plot")
def plot_hist(frame: pl.LazyFrame, figure: Figure = None, *, values: str, bin_width: float = 1.0, density: bool = False) -> Figure:
    """Histogram of one numeric column. `values` is the column name; `bin_width` sets the bucket size.

    behaviz's plot_hist1d is array-based (not column/`data=` like the other plots), so this node
    collects the column itself rather than going through `_render`.
    """
    _load_viz()
    df = _collect(frame)
    ax = figure[1] if isinstance(figure, tuple) and len(figure) == 2 else None
    return _bv.plot_hist1d(values=[df[values].to_numpy()], bin_width=bin_width, density=density, ax=ax)


# ponytail: no sink.plot.box -- behaviz plot_boxplot wants a numeric `x` axis position, not a
# category column, so it doesn't fit the "channel = column name" model the other plots use.
# @node("sink.plot.errorbar", "Plot")
# def plot_errorbar(frame: pl.LazyFrame, overrides:object =None, *, x:str, y:str, title:str="") -> dict:
