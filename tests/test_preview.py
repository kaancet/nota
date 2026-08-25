"""preview() payload for every value shape + truncation + json-safety."""

import datetime as dt
import json

import polars as pl

from nota.core.preview import preview, schema_of

LF = pl.LazyFrame({"g": ["a", "b", "a"], "v": [1, 2, 3]})


def test_lazy_truncated():
    p = preview(LF, n=2)
    assert p["columns"] == ["g", "v"]
    assert p["schema"] == {"g": "String", "v": "Int64"}
    assert p["rows"] == [["a", 1], ["b", 2]]
    assert p["shape"] == [None, 2]        # lazy + truncated -> unknown total
    assert p["truncated"] is True
    json.dumps(p)


def test_lazy_untruncated_knows_count():
    p = preview(LF, n=50)
    assert p["shape"] == [3, 2] and p["truncated"] is False


def test_eager_knows_count():
    p = preview(LF.collect(), n=50)
    assert p["shape"] == [3, 2]


def test_series_expr_scalar():
    assert preview(pl.Series("x", [1, 2]))["columns"] == ["x"]
    assert preview(pl.col("v").sum()) == {"type": "expr", "repr": 'col("v").sum()'}
    assert preview(42) == {"type": "scalar", "value": 42, "dtype": "int"}


def test_exotic_cells_json_safe():
    df = pl.DataFrame({"d": [dt.date(2020, 1, 1)], "lst": [[1, 2]]})
    p = preview(df)
    json.dumps(p)                          # dates/lists coerced to json-safe
    assert p["rows"][0][0] == "2020-01-01"


def test_schema_of():
    assert schema_of(LF) == {"g": "String", "v": "Int64"}
    assert schema_of(42) == {}
