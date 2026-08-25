"""Node registry + polars reflection.

A "node kind" = one callable + a schema describing its ports/params.
Two population sources, one mechanism:
  - reflect_polars(): auto-registers the whole polars API (Expr/DataFrame/... methods + pl.* funcs)
  - @node: registers a user/custom callable (sources, sinks, plugins)

The frontend never sees polars; it consumes manifest() (pure JSON) and edits a graph.
"""

from __future__ import annotations

import inspect
import logging
from dataclasses import dataclass
from typing import Any, Callable  # noqa: UP035

import polars as pl

loggr = logging.getLogger(__name__)

REGISTRY: dict[str, NodeSpec] = {}

# ---- slot kinds: how one signature parameter maps into a node --------------
RECEIVER = "receiver"  # method's `self` -> input port 0
PORT_OR_LITERAL = "port_lit"  # expr/frame arg: take a connection, else a literal param
VARIADIC_PORT = "variadic"  # *args of exprs -> a port accepting N connections
PARAM_REQ = "param_req"  # required primitive -> positional param widget
PARAM_KW = "param_kw"  # optional primitive -> keyword param widget


@dataclass
class Slot:
    kind: str
    name: str
    ptype: str  # "expr" | "frame" | "series" | "scalar"
    by_kw: bool = False  # param slots: pass by keyword (anything not positional-only)


@dataclass
class PortSpec:
    name: str
    type: str
    variadic: bool = False
    optional: bool = False


@dataclass
class ParamSpec:
    name: str
    type: str
    default: Any
    required: bool


@dataclass
class NodeSpec:
    kind: str
    category: str
    is_method: bool
    fn: Callable
    slots: list[Slot]
    inputs: list[PortSpec]
    params: list[ParamSpec]
    outputs: list[PortSpec]
    doc: str = ""
    namespace: str | None = None  # sub-namespace method: call getattr(receiver, namespace).method(...)

    def invoke(self, inbound: dict[str, list], params: dict) -> Any:
        """Reconstruct the call from wired inputs + params, in signature order."""
        args: list = []
        kwargs: dict = {}
        receiver = None
        for s in self.slots:
            if s.kind == RECEIVER:
                receiver = inbound[s.name][0]
            elif s.kind == PORT_OR_LITERAL:
                vals = inbound.get(s.name)
                if vals:
                    args.append(vals[0])
                elif params.get(s.name) is not None:
                    args.append(params[s.name])
                # else optional -> omit
            elif s.kind == VARIADIC_PORT:
                args.extend(inbound.get(s.name, []))
            elif s.kind == PARAM_REQ:  # noqa: SIM114
                (kwargs.__setitem__(s.name, params[s.name]) if s.by_kw else args.append(params[s.name]))
            elif s.kind == PARAM_KW and s.name in params:
                (kwargs.__setitem__(s.name, params[s.name]) if s.by_kw else args.append(params[s.name]))
        if self.is_method:
            target = getattr(receiver, self.namespace) if self.namespace else receiver
            return self.fn(target, *args, **kwargs)
        return self.fn(*args, **kwargs)

    def public(self) -> dict:
        """JSON-safe schema for the frontend manifest."""
        return {
            "kind": self.kind,
            "category": self.category,
            "inputs": [vars(p) for p in self.inputs],
            "params": [{**vars(p), "default": _jsonable(p.default)} for p in self.params],
            "outputs": [vars(p) for p in self.outputs],
            "doc": self.doc,
        }


# ---- annotation classification (polars uses string annotations) ------------
def _classify(ann: Any) -> str:
    a = str(ann)
    if "Expr" in a:
        return "expr"
    if "Frame" in a:  # DataFrame / LazyFrame
        return "frame"
    if "Series" in a:
        return "series"
    return "scalar"


def _prim(ann: Any) -> str:
    a = str(ann).lower()
    for t in ("bool", "int", "float", "str"):
        if t in a:
            return t
    return "any"


def _jsonable(v: Any) -> Any:
    if isinstance(v, (str, int, float, bool)) or v is None:
        return v
    if isinstance(v, (list, tuple)):
        return [_jsonable(x) for x in v]
    return str(v)


_FAMILY_TYPE = {"Expr": "expr", "Series": "series"}  # everything else -> "frame"


def build_spec(fn: Callable, kind: str, category: str, is_method: bool, namespace: str | None = None) -> NodeSpec:
    sig = inspect.signature(fn)
    params = list(sig.parameters.values())
    slots: list[Slot] = []
    inputs: list[PortSpec] = []
    pspecs: list[ParamSpec] = []

    if is_method:
        family = kind.split(".")[0]
        ftype = _FAMILY_TYPE.get(family, "frame")
        slots.append(Slot(RECEIVER, "self", ftype))
        inputs.append(PortSpec("self", ftype))
        params = params[1:]  # drop `self`

    for p in params:
        if p.kind == p.VAR_POSITIONAL:
            t = _classify(p.annotation)
            slots.append(Slot(VARIADIC_PORT, p.name, t))
            inputs.append(PortSpec(p.name, t, variadic=True, optional=True))
        elif p.kind == p.VAR_KEYWORD:
            continue  # ignore **kwargs for the prototype
        elif _classify(p.annotation) in ("expr", "frame", "series"):
            t = _classify(p.annotation)
            slots.append(Slot(PORT_OR_LITERAL, p.name, t))
            inputs.append(PortSpec(p.name, t, optional=True))
            pspecs.append(ParamSpec(p.name, "any", _default(p), False))
        elif p.default is inspect._empty:
            slots.append(Slot(PARAM_REQ, p.name, "scalar", p.kind != p.POSITIONAL_ONLY))
            pspecs.append(ParamSpec(p.name, _prim(p.annotation), None, True))
        else:
            slots.append(Slot(PARAM_KW, p.name, "scalar", p.kind != p.POSITIONAL_ONLY))
            pspecs.append(ParamSpec(p.name, _prim(p.annotation), _default(p), False))

    outputs = [PortSpec("out", _classify(sig.return_annotation))]
    doc = (inspect.getdoc(fn) or "").split("\n\n")[0]
    return NodeSpec(kind, category, is_method, fn, slots, inputs, pspecs, outputs, doc, namespace)


def _default(p) -> Any:
    return None if p.default is inspect._empty else p.default


def register(fn: Callable, kind: str, category: str, is_method: bool, namespace: str | None = None) -> None:
    try:
        REGISTRY[kind] = build_spec(fn, kind, category, is_method, namespace)
    except (ValueError, TypeError) as e:  # unintrospectable signature -> skip
        loggr.debug("skip %s: %s", kind, e)


def node(kind: str, category: str = "Custom") -> Callable:
    """Decorator: register a user function as a node kind. Schema from its signature."""

    def deco(fn: Callable) -> Callable:
        register(fn, kind, category, is_method=False)
        return fn

    return deco


# ---- reflect the whole polars surface --------------------------------------
def _families() -> dict[str, type]:
    fams: dict[str, type] = {
        "Expr": pl.Expr,
        "DataFrame": pl.DataFrame,
        "LazyFrame": pl.LazyFrame,
        "Series": pl.Series,
    }
    try:
        from polars.dataframe.group_by import GroupBy
        from polars.lazyframe.group_by import LazyGroupBy

        fams["LazyGroupBy"] = LazyGroupBy
        fams["GroupBy"] = GroupBy
    except ImportError:  # pragma: no cover - version drift
        pass
    return fams


def _register_overrides() -> None:
    """Curation layer: hand-specs for members whose real signature can't be
    reflected (magic objects like pl.col whose attribute access is hijacked).
    This is where the ~10% of messy polars API gets fixed up."""

    def col(name: str) -> pl.Expr:
        return pl.col(name)

    register(col, "pl.col", "pl", is_method=False)


# Expr sub-namespaces: properties (Expr.str, Expr.dt, ...) returning namespace objects.
# isfunction misses them, so reflect each namespace class separately.
_EXPR_NAMESPACES = ["str", "dt", "list", "arr", "struct", "cat", "bin", "name"]


def _reflect_namespaces() -> None:
    probe = pl.col("_")  # a throwaway Expr to reach each namespace class
    for ns in _EXPR_NAMESPACES:
        try:
            cls = type(getattr(probe, ns))
        except AttributeError:  # namespace absent in this polars version
            continue
        for name, fn in inspect.getmembers(cls, inspect.isfunction):
            if name.startswith("_"):
                continue
            register(fn, f"Expr.{ns}.{name}", f"Expr.{ns}", is_method=True, namespace=ns)


def reflect_polars() -> None:
    """Populate REGISTRY from installed polars: methods + top-level functions."""
    for family, cls in _families().items():
        for name, fn in inspect.getmembers(cls, inspect.isfunction):
            if name.startswith("_"):
                continue
            register(fn, f"{family}.{name}", family, is_method=True)

    for name, fn in inspect.getmembers(pl, inspect.isfunction):
        if name.startswith("_"):
            continue
        register(fn, f"pl.{name}", "pl", is_method=False)

    _reflect_namespaces()
    _register_overrides()


def manifest(tier: str = "all") -> list[dict]:
    """The frontend-facing node catalog: curated (tier/category/label), hidden noise
    dropped. Pure JSON, no polars objects. tier="common" returns only promoted nodes.
    """
    from .curation import decorate

    out = []
    for spec in REGISTRY.values():
        e = decorate(spec.public())
        if e is None or (tier == "common" and e["tier"] != "common"):
            continue
        out.append(e)
    return out


def catalog(tier: str = "common") -> dict[str, list[dict]]:
    """Manifest grouped by category -> entries, for a palette tree."""
    tree: dict[str, list[dict]] = {}
    for e in manifest(tier):
        tree.setdefault(e["category"], []).append(e)
    return tree
