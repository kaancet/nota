"""import_function: register a user .py function as an 'Imported' node."""

import nota.nodes  # noqa: F401  (source.sample / sink.preview)
import pytest

from nota.core import Graph, import_function, run

_FN = """import polars as pl

def scale_score(frame: pl.LazyFrame, *, factor: float = 2.0) -> pl.LazyFrame:
    "Multiply the score column by a factor."
    return frame.with_columns((pl.col("score") * factor).alias("score"))
"""


def test_import_reflects_and_runs(tmp_path):
    f = tmp_path / "my_scale.py"
    f.write_text(_FN)
    e = import_function(str(f))
    assert e["kind"] == "imported.scale_score"
    assert e["category"] == "Imported" and e["tier"] == "common"
    assert e["inputs"][0]["type"] == "frame"                 # frame -> input port
    assert any(p["name"] == "factor" and p["type"] == "float" for p in e["params"])
    assert e["outputs"][0]["type"] == "frame"

    g = (Graph().add("src", "source.sample").add("sc", "imported.scale_score", {"factor": 10.0})
         .add("pv", "sink.preview"))
    g.connect("src", "sc", "frame")
    g.connect("sc", "pv", "frame")
    assert run(g)["pv"]["rows"][0][2] == 85.0                # score 8.5 * 10


def test_import_rejects_not_exactly_one_function(tmp_path):
    f = tmp_path / "two.py"
    f.write_text("def a():\n    pass\n\ndef b():\n    pass\n")
    with pytest.raises(ValueError):
        import_function(str(f))
    missing = tmp_path / "nope.py"
    with pytest.raises(FileNotFoundError):
        import_function(str(missing))
