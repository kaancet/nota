"""Layer B enforcement -- every builtin node MUST have a @covers behavioral test.
This is the strict gate: add a node, forget its test, the suite goes red here.
"""

from helpers import BUILTIN_KINDS


def test_every_builtin_has_a_behavioral_test(request):
    covered = getattr(request.config, "_covered_kinds", set())
    missing = set(BUILTIN_KINDS) - covered
    assert not missing, (
        "These builtin nodes have no @covers behavioral test: "
        f"{sorted(missing)}\n"
        "Add one in tests/ (see tests/TEMPLATE.py and tests/README.md)."
    )
