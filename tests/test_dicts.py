"""dict.build: assemble a dict from key/value rows, optionally extending a base dict."""

from helpers import covers

from nota.core import Graph, manifest, run


@covers("dict.build")
def test_dict_build_from_rows():
    g = Graph().add("d", "dict.build", {"entries": [["a", 1], ["b", "x"], ["", 9]]})
    assert run(g)["d"] == {"a": 1, "b": "x"}   # blank-key row skipped


def test_dict_build_extends_base():
    g = (Graph()
         .add("base", "dict.build", {"entries": [["x", 1]]})
         .add("d", "dict.build", {"entries": [["y", 2]]}))
    g.connect("base", "d", "base")
    assert run(g)["d"] == {"x": 1, "y": 2}


def test_dict_build_manifest_widget_hint():
    e = next(m for m in manifest("common") if m["kind"] == "dict.build")
    assert e["category"] == "Dict"
    entries = next(p for p in e["params"] if p["name"] == "entries")
    assert entries["widget"] == "kvlist"                 # frontend renders the row editor
    assert "base" in {p["name"] for p in e["inputs"]}    # wireable base-dict port
