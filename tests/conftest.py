"""pytest wiring: register the `covers` marker and collect which builtin kinds
each test claims to cover (read by the coverage guard)."""

import os
import tempfile


def pytest_configure(config):
    config.addinivalue_line("markers", "covers(kind): behavioral test for a builtin node kind")
    # Hermetic: tests never read ~/.nota/nodes
    os.environ.setdefault("NOTA_USER_NODES", tempfile.mkdtemp(prefix="nota_test_"))


def pytest_collection_modifyitems(config, items):
    covered = set()
    for item in items:
        for mark in item.iter_markers("covers"):
            covered |= set(mark.args)
    config._covered_kinds = covered
