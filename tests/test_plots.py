"""Plot sinks: a frame -> a self-contained interactive HTML payload (bokeh via behaviz)."""

from helpers import covers, preview_of

from nota.core import Graph, manifest


def _plot(kind: str) -> dict:
    """Run test.source -> a plot node and return its HTML payload."""
    g = Graph().add("src", "test.source").add("pl", kind, {"x": "v", "y": "v", "title": "T"})
    g.connect("src", "pl", "frame")
    return preview_of(g, "pl")


def _assert_inline_html(out: dict) -> None:
    assert out["type"] == "html"
    assert out["html"].lstrip().startswith("<!DOCTYPE")   # standalone document
    assert "Bokeh" in out["html"]                          # BokehJS inlined -> works offline


@covers("sink.plot.line")
def test_plot_line():
    _assert_inline_html(_plot("sink.plot.line"))


@covers("sink.plot.scatter")
def test_plot_scatter():
    _assert_inline_html(_plot("sink.plot.scatter"))


@covers("sink.plot.bar")
def test_plot_bar():
    _assert_inline_html(_plot("sink.plot.bar"))


@covers("sink.plot.step")
def test_plot_step():
    _assert_inline_html(_plot("sink.plot.step"))


def test_plot_nodes_are_common_and_shaped():
    common = {e["kind"]: e for e in manifest("common")}
    line = common["sink.plot.line"]
    assert line["category"] == "Plot"
    # x/y/title are param widgets; frame + overrides (the un-hidden kwargs dict) are input ports
    assert {"x", "y", "title"} <= {p["name"] for p in line["params"]}
    assert {"frame", "overrides"} <= {p["name"] for p in line["inputs"]}
    assert next(p for p in line["params"] if p["name"] == "x")["required"] is True
