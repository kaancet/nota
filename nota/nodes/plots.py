"""Plot sink nodes -- render a frame to an interactive bokeh plot (standalone HTML).

behaviz builds the figure on its bokeh backend; bokeh's ``file_html`` inlines BokehJS so
the payload is self-contained (works offline in a webview, no CDN). Channels (``x``/``y``/…)
are column names resolved against the frame. ``overrides`` is an optional dict input for ANY
backend styling kwarg -- so nothing is hidden: wire a ``dict.build`` node into it. Output is
a ``{"type": "html"}`` preview payload the frontend renders in a webview.
"""

from __future__ import annotations

import behaviz as bv
import polars as pl
from bokeh.embed import file_html
from bokeh.resources import INLINE

from nota.core.registry import node

bv.set_renderer("bokeh")  # process-global; plotting is behaviz's only use in the server

_ROWCAP = 10_000  # plots don't need millions of points -> cap the materialize


def _collect(frame: object) -> pl.DataFrame:
    if isinstance(frame, pl.LazyFrame):
        return frame.head(_ROWCAP).collect()
    if isinstance(frame, pl.DataFrame):
        return frame.head(_ROWCAP)
    return frame  # already array-like / dict -> let behaviz resolve it


def _render(fn, frame: object, overrides: object, *, title: str, **channels) -> dict:
    """Run a behaviz plot fn and wrap its figure as an inline-HTML preview payload.
    `channels` = the plot's column mappings; `overrides` = a dict of backend styling kwargs."""
    spec = bv.PlotSpec(title=title) if title else None
    fig, _ax = fn(data=_collect(frame), spec=spec, **channels, **(overrides or {}))
    return {"type": "html", "html": file_html(fig, INLINE, title or "plot")}


@node("sink.plot.line", "Plot")
def plot_line(frame: pl.LazyFrame, overrides: object = None, *, x: str, y: str, title: str = "") -> dict:
    """Interactive line plot. `x`/`y` are column names; wire a dict into `overrides` for any bokeh styling kwarg."""
    return _render(bv.plot_line, frame, overrides, x=x, y=y, title=title)


@node("sink.plot.scatter", "Plot")
def plot_scatter(frame: pl.LazyFrame, overrides: object = None, *, x: str, y: str, title: str = "") -> dict:
    """Interactive scatter plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render(bv.plot_scatter, frame, overrides, x=x, y=y, title=title)


@node("sink.plot.bar", "Plot")
def plot_bar(frame: pl.LazyFrame, overrides: object = None, *, x: str, y: str, title: str = "") -> dict:
    """Interactive bar plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render(bv.plot_bar, frame, overrides, x=x, y=y, title=title)


@node("sink.plot.step", "Plot")
def plot_step(frame: pl.LazyFrame, overrides: object = None, *, x: str, y: str, title: str = "") -> dict:
    """Interactive step plot. `x`/`y` are column names; `overrides` forwards any bokeh styling kwarg."""
    return _render(bv.plot_step, frame, overrides, x=x, y=y, title=title)
