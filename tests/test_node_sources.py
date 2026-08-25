"""Behavioral tests for the source.* builtin nodes."""

import polars as pl
import pytest
from helpers import covers, preview_of

from nota.core import Graph

DF = pl.DataFrame({"g": ["a", "b"], "v": [1, 2]})
ROWS = [["a", 1], ["b", 2]]


def _scan(kind: str, path: str, **params) -> list:
    g = Graph().add("src", kind, {"path": path, **params}).add("pv", "sink.preview")
    g.connect("src", "pv", "frame")
    return preview_of(g)["rows"]


@covers("source.csv")
def test_csv(tmp_path):
    f = tmp_path / "x.csv"
    DF.write_csv(f)
    assert _scan("source.csv", str(f)) == ROWS


@covers("source.parquet")
def test_parquet(tmp_path):
    f = tmp_path / "x.parquet"
    DF.write_parquet(f)
    assert _scan("source.parquet", str(f)) == ROWS


@covers("source.ndjson")
def test_ndjson(tmp_path):
    f = tmp_path / "x.ndjson"
    DF.write_ndjson(f)
    assert _scan("source.ndjson", str(f)) == ROWS


@covers("source.ipc")
def test_ipc(tmp_path):
    f = tmp_path / "x.arrow"
    DF.write_ipc(f)
    assert _scan("source.ipc", str(f)) == ROWS


@covers("source.auto")
def test_auto_dispatch(tmp_path):
    f = tmp_path / "x.parquet"
    DF.write_parquet(f)
    assert _scan("source.auto", str(f)) == ROWS
    t = tmp_path / "x.tsv"
    DF.write_csv(t, separator="\t")
    assert _scan("source.auto", str(t)) == ROWS  # tsv -> tab separator


@covers("source.excel")
def test_excel(tmp_path):
    pytest.importorskip("fastexcel")
    pytest.importorskip("xlsxwriter")
    f = tmp_path / "x.xlsx"
    DF.write_excel(f)
    assert _scan("source.excel", str(f)) == ROWS


def test_missing_file_raises_early(tmp_path):
    with pytest.raises(FileNotFoundError):
        _scan("source.csv", str(tmp_path / "nope.csv"))
