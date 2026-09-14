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


def _render(plot_name: str, frame: pl.LazyFrame, overrides: dict, figure: object = None, *, title: str, **channels) -> tuple:
    """Run a behaviz plot fn, return (fig, ax) for downstream overlay."""
    _load_viz()
    fn = getattr(_bv, plot_name)
    spec = _bv.PlotSpec(title=title) if title else None
    ax = figure[1] if isinstance(figure, tuple) and len(figure) == 2 else None
    return fn(data=_collect(frame), ax=ax, spec=spec, **channels, **(overrides or {}))


@node("sink.plot.line", "Plot")
def plot_line(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, title: str = "") -> Figure:
    """Line plot. `x`/`y` are column names; wire a dict into `overrides` for any bokeh styling kwarg."""
    return _render("plot_line", frame, overrides, figure, x=x, y=y, title=title)


@node("sink.plot.scatter", "Plot")
def plot_scatter(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, title: str = "") -> Figure:
    """Scatter plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render("plot_scatter", frame, overrides, figure, x=x, y=y, title=title)


@node("sink.plot.bar", "Plot")
def plot_bar(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, title: str = "") -> Figure:
    """Bar plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render("plot_bar", frame, overrides, figure, x=x, y=y, title=title)


@node("sink.plot.step", "Plot")
def plot_step(frame: pl.LazyFrame, overrides: object = None, figure: Figure = None, *, x: str, y: str, title: str = "") -> Figure:
    """Step plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render("plot_step", frame, overrides, figure, x=x, y=y, title=title)


# @node("sink.plot.errorbar", "Plot")
# def plot_errorbar(frame: pl.LazyFrame, overrides:object =None, *, x:str, y:str, title:str="") -> dict:
