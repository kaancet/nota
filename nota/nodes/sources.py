"""Source nodes -- get data in. All lazy (scan_*), so the pipeline stays a plan
until a sink collects. Existence is checked up front for an immediate error instead
of a confusing failure deep inside a later collect.
"""

from pathlib import Path

import polars as pl

from nota.core.registry import node


def _require(path: str) -> str:
    if "*" not in path and not Path(path).exists():   # "*" -> a glob, let polars resolve it
        raise FileNotFoundError(f"no such file: {path}")
    return path


@node("source.sample", "Source")
def sample() -> pl.LazyFrame:
    """A small built-in sample table (no file needed) -- handy for demos and testing."""
    return pl.LazyFrame({
        "id": [1, 2, 3, 4, 5],
        "name": ["ana", "bob", "cy", "dan", "eve"],
        "score": [8.5, 6.0, 9.1, 7.2, 5.4],
    })


@node("source.csv", "Source")
def read_csv(path: str, *, separator: str = ",", has_header: bool = True) -> pl.LazyFrame:
    """Scan a CSV/TSV file lazily."""
    return pl.scan_csv(_require(path), separator=separator, has_header=has_header)


@node("source.parquet", "Source")
def read_parquet(path: str) -> pl.LazyFrame:
    """Scan a Parquet file lazily."""
    return pl.scan_parquet(_require(path))


@node("source.ndjson", "Source")
def read_ndjson(path: str) -> pl.LazyFrame:
    """Scan a newline-delimited JSON file lazily."""
    return pl.scan_ndjson(_require(path))


@node("source.ipc", "Source")
def read_ipc(path: str) -> pl.LazyFrame:
    """Scan an Arrow IPC / Feather file lazily."""
    return pl.scan_ipc(_require(path))


@node("source.excel", "Source")
def read_excel(path: str, *, sheet_name: str = "") -> pl.LazyFrame:
    """Read an Excel sheet (eager -> .lazy()). Needs the 'fastexcel' extra."""
    df = pl.read_excel(_require(path), sheet_name=sheet_name or None)
    return df.lazy()


_BY_EXT = {
    ".csv": read_csv, ".tsv": read_csv,
    ".parquet": read_parquet, ".pq": read_parquet,
    ".json": read_ndjson, ".ndjson": read_ndjson,
    ".arrow": read_ipc, ".ipc": read_ipc, ".feather": read_ipc,
    ".xlsx": read_excel, ".xls": read_excel,
}


@node("source.auto", "Source")
def read_auto(path: str) -> pl.LazyFrame:
    """Pick the reader by file extension."""
    ext = Path(path).suffix.lower()
    reader = _BY_EXT.get(ext)
    if reader is None:
        raise ValueError(f"unknown extension {ext!r}; known: {sorted(_BY_EXT)}")
    kw = {"separator": "\t"} if ext == ".tsv" else {}
    return reader(path, **kw)
