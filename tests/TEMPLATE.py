"""COPY ME when you add a builtin node. (This file is not collected -- no test_ prefix.)

Every builtin node (anything registered from a nota.nodes module) MUST have a
behavioral test marked with @covers("<kind>"). The coverage guard fails the suite
otherwise. Structure: arrange -> act -> assert, one @covers per node kind.
"""

from helpers import covers, preview_of

from nota.core import Graph


@covers("your.node.kind")                      # <-- REQUIRED: the kind string you registered
def test_your_node_does_the_thing(tmp_path):
    # ARRANGE: build the smallest graph that exercises the node.
    #   - use "test.source" as an in-memory frame source (columns g:str, v:int)
    #   - use tmp_path for any file the node reads/writes
    g = Graph().add("src", "test.source")
    g.add("n", "your.node.kind", {"some_param": 1})
    g.add("pv", "sink.preview")
    g.connect("src", "n", "self")              # name the port when a node has >1 input
    g.connect("n", "pv")

    # ACT: run and read the sink payload.
    out = preview_of(g)                         # == run(g)["pv"]

    # ASSERT: pin the behavior you care about (columns / rows / shape / written file).
    assert out["columns"] == ["g", "v"]
    assert out["rows"] == [["a", 1], ["b", 2], ["a", 3]]
