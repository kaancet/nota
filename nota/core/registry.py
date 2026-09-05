"""Node registry and the polars reflection.

A "node kind" is one callable and a schema describing its ports/params.
Two population sources:
  - reflect_polars(): auto-registers the whole polars API (Expr/DataFrame/... methods + pl.* funcs)
  - @node: registers a user/custom callable (sources, sinks, plots, plugins)

Avalonia consumes manifest() (pure JSON) and edits a graph.
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
    doc: str = ""  # per-port doc from the docstring's Parameters section


@dataclass
class ParamSpec:
    name: str
    type: str
    default: Any
    required: bool
    doc: str = ""  # per-param doc from the docstring's Parameters section
    choices: list | None = None  # enum values (from a Literal annotation) becomes a dropdown in the UI


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
    examples: str = ""  # docstring Examples section, verbatim
    doc_url: str | None = None  # link to the polars API page, when one is derivable
    namespace: str | None = None  # sub-namespace method: call getattr(receiver, namespace).method(...)

    def invoke(self, inbound: dict[str, list], params: dict) -> Any:
        """Reconstruct the call from wired inputs and params, in signature order.

        Args:
            inbound (dict[str, list]): Inputs dictionary
            params (dict): Parameter dictionary

        Returns:
            Any: The invoked function call
        """
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
        """JSON-safe schema for the frontend manifest to build the node UI"""
        return {
            "kind": self.kind,
            "category": self.category,
            "inputs": [vars(p) for p in self.inputs],
            "params": [
                {
                    k: v
                    for k, v in {**vars(p), "default": _jsonable(p.default)}.items()
                    if not (k == "choices" and v is None)
                }  # omit choices unless present
                for p in self.params
            ],
            "outputs": [vars(p) for p in self.outputs],
            "doc": self.doc,
            "examples": self.examples,
            "doc_url": self.doc_url,
        }


def _classify(ann: Any) -> str:
    """Classifies kind/type annotations to plain strings annotation

    Args:
        ann (Any): annotation

    Returns:
        str: kind classification name
    """
    a = str(ann)
    if "Expr" in a:
        return "expr"
    if "Frame" in a:  # DataFrame / LazyFrame
        return "frame"
    if "Series" in a:
        return "series"
    if a in ("<class 'object'>", "object"):
        return "any"  # an explicit `object` annotation -> a port that accepts anything
    return "scalar"


def _prim(ann: Any) -> str:
    """Classifies data type(integer,float,etc) to plain strings from annotation

    Args:
        ann (Any): annotation

    Returns:
        str: dta atype classification
    """
    a = str(ann).lower()
    for t in ("bool", "int", "float", "str"):
        if t in a:
            return t
    return "any"


def _ann_ns() -> dict:
    """Adds the polars Literals into typing.Literals so they can appear as Enums in some nodes

    Returns:
        dict: A dictionary containing allowed/existing Literals
    """
    import typing

    ns: dict = {"Literal": typing.Literal}
    try:
        import polars._typing as plt  # private, but where the Literal aliases live

        ns.update(vars(plt))
    except Exception:  # noqa: BLE001, S110 - version drift; reflection just won't find choices
        pass
    return ns


_ANN_NS = _ann_ns()


def _literal_choices(ann: Any) -> list | None:
    """The allowed string values if `ann` is (or wraps, e.g. `X | None`) a Literal of strings.

    Args:
        ann (Any): annotation

    Returns:
        list | None: list of string values allowed
    """
    import typing

    if ann is inspect._empty:
        return None
    try:
        t = eval(ann, _ANN_NS) if isinstance(ann, str) else ann
    except Exception:  # noqa: BLE001 - unresolvable annotation -> no choices
        return None
    args = None
    if typing.get_origin(t) is typing.Literal:
        args = typing.get_args(t)
    else:  # unwrap Optional / unions -> find a Literal member
        for a in typing.get_args(t):
            if typing.get_origin(a) is typing.Literal:
                args = typing.get_args(a)
                break
    return list(args) if args and all(isinstance(x, str) for x in args) else None


def _jsonable(v: Any) -> Any:
    """JSONifies the input

    Args:
        v (Any): variable to be jsonified

    Returns:
        Any: jsonified output
    """
    if isinstance(v, (str, int, float, bool)) or v is None:
        return v
    if isinstance(v, (list, tuple)):
        return [_jsonable(x) for x in v]
    return str(v)


_FAMILY_TYPE = {"Expr": "expr", "Series": "series"}  # everything else -> "frame"

# family -> the polars docs reference subpath its members live under
_DOC_GROUP = {"Expr": "expressions", "LazyFrame": "lazyframe", "DataFrame": "dataframe", "Series": "series"}


def _doc_url(kind: str) -> str | None:
    """Best-effort polars API page for a reflected member; None for custom nodes.
    Currently only the four main families are mapped (covers every COMMON node).
    pl.* top-level funcs are omitted rather than guess their scattered subpaths.

    Args:
        kind (str): node kind as a string

    Returns:
        str | None: Link to polars API page
    """
    group = _DOC_GROUP.get(kind.split(".")[0])
    if group is None:
        return None
    return f"https://docs.pola.rs/api/python/stable/reference/{group}/api/polars.{kind}.html"


def _parse_numpydoc(doc: str) -> tuple[dict[str, str], str]:
    """Return ({param_name: blurb}, examples_text) from a numpydoc-style docstring."""
    lines = doc.splitlines()
    sections: dict[str, list[str]] = {}
    cur: str | None = None
    i = 0
    while i < len(lines):
        nxt = lines[i + 1].strip() if i + 1 < len(lines) else ""
        if nxt and set(nxt) == {"-"}:  # a "Title\n-----" section header
            cur = lines[i].strip().lower()
            sections[cur] = []
            i += 2
            continue
        if cur is not None:
            sections[cur].append(lines[i])
        i += 1
    return _parse_param_block(sections.get("parameters", [])), "\n".join(sections.get("examples", [])).strip()


def _parse_param_block(block: list[str]) -> dict[str, str]:
    """numpydoc Parameters: a flush-left `name [: type]` header, then indented description.
    A header may list several comma-separated names sharing one description."""
    out: dict[str, str] = {}
    names: list[str] = []
    desc: list[str] = []

    def flush() -> None:
        text = " ".join(w.strip() for w in desc).strip()
        for n in names:
            out[n] = text

    for line in block:
        if line and not line[0].isspace():  # flush-left, non-blank -> a new entry header
            flush()
            desc = []
            names = [n.strip() for n in line.split(":")[0].split(",") if n.strip()]
        elif names:
            desc.append(line)
    flush()
    return out


def build_spec(fn: Callable, kind: str, category: str, is_method: bool, namespace: str | None = None) -> NodeSpec:
    sig = inspect.signature(fn)
    param_docs, examples = _parse_numpydoc(inspect.getdoc(fn) or "")
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
        elif _classify(p.annotation) in ("expr", "frame", "series", "any"):
            t = _classify(p.annotation)
            slots.append(Slot(PORT_OR_LITERAL, p.name, t))
            inputs.append(PortSpec(p.name, t, optional=True))
            pspecs.append(ParamSpec(p.name, "any", _default(p), False))
        elif p.default is inspect._empty:
            slots.append(Slot(PARAM_REQ, p.name, "scalar", p.kind != p.POSITIONAL_ONLY))
            choices = _literal_choices(p.annotation)
            pspecs.append(ParamSpec(p.name, "str" if choices else _prim(p.annotation), None, True, choices=choices))
        else:
            slots.append(Slot(PARAM_KW, p.name, "scalar", p.kind != p.POSITIONAL_ONLY))
            choices = _literal_choices(p.annotation)
            pspecs.append(
                ParamSpec(p.name, "str" if choices else _prim(p.annotation), _default(p), False, choices=choices)
            )

    for p in inputs:
        p.doc = param_docs.get(p.name, "")
    for p in pspecs:
        p.doc = param_docs.get(p.name, "")

    outputs = [PortSpec("out", _classify(sig.return_annotation))]
    doc = (inspect.getdoc(fn) or "").split("\n\n")[0]
    return NodeSpec(
        kind, category, is_method, fn, slots, inputs, pspecs, outputs, doc, examples, _doc_url(kind), namespace
    )


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


def import_function(path: str) -> dict:
    """Load a .py file holding a single top-level function and register it as an 'Imported' node.

    The function's signature is reflected exactly like any builtin (frame/expr/series/object
    annotations -> ports; primitives -> param widgets; return annotation -> output port). Returns
    a manifest entry the frontend drops into the palette. Executes the file -> only import files you trust.
    """
    import importlib.util
    from pathlib import Path

    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(f"no such file: {path}")
    spec = importlib.util.spec_from_file_location(f"nota_imported_{p.stem}", str(p))
    if spec is None or spec.loader is None:
        raise ValueError(f"cannot load {path}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)  # runs the file's top level

    fns = [
        f
        for name, f in inspect.getmembers(mod, inspect.isfunction)
        if f.__module__ == mod.__name__ and not name.startswith("_")
    ]
    if len(fns) != 1:
        raise ValueError(f"expected exactly one top-level function in {p.name}, found {len(fns)}")

    fn = fns[0]
    kind = f"imported.{fn.__name__}"
    REGISTRY[kind] = build_spec(fn, kind, "Imported", is_method=False)  # raises on an unintrospectable signature
    entry = REGISTRY[kind].public()
    entry["category"] = "Imported"
    entry["label"] = fn.__name__.replace("_", " ")
    entry["tier"] = "common"
    return entry


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
