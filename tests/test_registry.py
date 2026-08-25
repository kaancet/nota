"""Registry + reflection machinery. NOT polars correctness -- just that our
slot-classification, invoke reconstruction, reflection, and overrides work.
A few reflected kinds act as canaries; if they pass, the 700+ do too.
"""

import inspect

from nota.core import REGISTRY, node
from nota.core.registry import (
    PARAM_KW,
    PORT_OR_LITERAL,
    RECEIVER,
    VARIADIC_PORT,
    build_spec,
)


def test_reflection_canaries_present():
    for kind in ("Expr.sum", "LazyFrame.filter", "LazyGroupBy.agg", "pl.lit"):
        assert kind in REGISTRY
    assert len(REGISTRY) > 500                        # the polars surface is exposed


def test_pl_col_override_present():
    assert "pl.col" in REGISTRY                        # unreflectable magic obj -> override


def test_namespace_reflected_and_tagged():
    spec = REGISTRY.get("Expr.str.contains")
    assert spec is not None and spec.namespace == "str"


def test_slot_classification():
    # filter(self, *predicates, **constraints): receiver + variadic, **kwargs dropped
    slots = {s.kind for s in REGISTRY["LazyFrame.filter"].slots}
    assert RECEIVER in slots and VARIADIC_PORT in slots


def test_keyword_only_param_is_by_kw():
    # a keyword-only required param must be passed as a kwarg, not positionally
    def fn(frame, *, path: str) -> str:  # noqa: ARG001
        return path
    spec = build_spec(fn, "x.kwtest", "Test", is_method=False)
    path_slot = next(s for s in spec.slots if s.name == "path")
    assert path_slot.by_kw is True


def test_bad_signature_is_skipped_not_crashed():
    # register() swallows unintrospectable callables
    from nota.core.registry import register
    class Weird:
        __signature__ = property(lambda self: 1 / 0)   # blows up on inspect
    before = len(REGISTRY)
    register(Weird(), "x.weird", "Test", is_method=False)
    assert len(REGISTRY) == before                     # skipped, no exception


def test_node_decorator_registers_with_schema():
    @node("x.decotest", "Test")
    def f(a: int, *, b: str = "z") -> int:  # noqa: ARG001
        return a
    spec = REGISTRY["x.decotest"]
    params = {p.name for p in spec.params}
    assert params == {"a", "b"} and spec.outputs[0].name == "out"
