"""pytest wiring: register the `covers` marker and collect which builtin kinds
each test claims to cover (read by the coverage guard)."""


def pytest_configure(config):
    config.addinivalue_line("markers", "covers(kind): behavioral test for a builtin node kind")


def pytest_collection_modifyitems(config, items):
    covered = set()
    for item in items:
        for mark in item.iter_markers("covers"):
            covered |= set(mark.args)
    config._covered_kinds = covered
