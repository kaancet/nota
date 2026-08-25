# Tests

```bash
uv pip install -e ".[dev]"
.venv/bin/pytest
```

## The one rule

**Every builtin node needs a behavioral test.** A "builtin" is any node registered
from a `nota.nodes` module (source/sink/column/literal/…). Reflected polars ops and
user-defined nodes are *not* builtins and need no test here.

Add a builtin → `tests/test_coverage_guard.py` goes red until you write its test.

## How to write one

1. Copy [`TEMPLATE.py`](TEMPLATE.py) into a `test_node_*.py` file.
2. Mark it `@covers("your.kind")` — this is what the guard counts.
3. arrange → act → assert. Use the shared helpers:
   - `preview_of(g)` — run the graph, return the sink payload
   - `test.source` — an in-memory source (`g:str`, `v:int`), no file needed
   - `tmp_path` — pytest's temp dir for file I/O

## Layers

| file | what | who maintains |
|---|---|---|
| `test_contract.py` | generic schema/manifest checks, **auto-parametrized over every builtin** | nobody — automatic |
| `test_coverage_guard.py` | fails if a builtin lacks a `@covers` test | nobody — automatic |
| `test_node_*.py` | **behavioral, one `@covers` per builtin** | **you, when adding a node** |
| `test_engine / registry / preview / curation` | core machinery | on change |
| `test_regressions.py` | one test per bug already fixed | append when you fix a bug |

## What we do NOT test

- **polars correctness** — that's polars' job. A few reflected kinds are canaries in
  `test_registry.py`; if they pass, the rest work.
- **user node logic** — the user's job. We only test the `@node` mechanism.
