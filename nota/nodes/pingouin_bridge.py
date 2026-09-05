"""pingouin stats as nodes, applied to polars LazyFrames.

pingouin works on pandas DataFrames / arrays. Each node here bridges at the boundary:
LazyFrame -> collect().to_pandas() -> pingouin -> a polars frame back. Two families,
reflected from the installed pingouin (no annotations there, so a custom spec, not build_spec):

  data-style  (fn has a `data=` param): the frame is the data; the rest reflect as params.
              invoke -> fn(data=pdf, **params).
  array-style (fn takes x/y arrays): a translation layer -> the frame is the data + one
              column-name param per array arg; invoke -> fn(pdf[xcol], pdf[ycol], **params).

Both normalize the result to a polars frame, so a pingouin node acts like any other node.
"""

from __future__ import annotations

import inspect
import logging
from dataclasses import dataclass, field
from typing import Any, Callable  # noqa: UP035

import polars as pl

from nota.core.registry import REGISTRY, ParamSpec, PortSpec

loggr = logging.getLogger(__name__)

# array-style functions worth exposing -> the positional args that are arrays (fed as columns).
# (array-style funcs need a manual column mapping; data-style funcs are all reflected.)
ARRAY_ARGS: dict[str, list[str]] = {
    "ttest": ["x", "y"], "mwu": ["x", "y"], "wilcoxon": ["x", "y"], "corr": ["x", "y"],
    "tost": ["x", "y"], "compute_effsize": ["x", "y"], "anderson": ["x"],
    "linear_regression": ["X", "y"], "logistic_regression": ["X", "y"], "multivariate_ttest": ["X"],
}

# nicer dropdowns for the string options pingouin doesn't annotate (kept conservative)
CHOICES: dict[str, list[str]] = {
    "alternative": ["two-sided", "greater", "less"],
    "padjust": ["none", "bonf", "sidak", "holm", "fdr_bh", "fdr_by"],
    "eftype": ["cohen", "hedges", "r", "pointbiserialr", "eta-square", "odds-ratio", "AUC", "CLES"],
    "effsize": ["none", "hedges", "cohen", "r", "eta-square", "odds-ratio", "AUC", "CLES"],
}


def _to_frame(res: Any) -> pl.LazyFrame:
    """Normalize any pingouin return (DataFrame / Series / array / tuple / scalar) to a polars frame."""
    import numpy as np
    import pandas as pd

    if isinstance(res, pl.LazyFrame):
        return res
    if isinstance(res, pl.DataFrame):
        return res.lazy()
    if isinstance(res, (pd.DataFrame, pd.Series)):
        return pl.from_pandas(res.reset_index()).lazy()   # keep the index (row labels carry meaning)
    if isinstance(res, np.ndarray):
        col = res.tolist() if res.ndim == 1 else [list(r) for r in res]
        return pl.DataFrame({"value": col}).lazy()
    if isinstance(res, tuple):
        return pl.DataFrame({"stat": [str(x) for x in res]}).lazy()
    return pl.DataFrame({"value": [res]}).lazy()          # a plain scalar


def _pspec(p: inspect.Parameter) -> ParamSpec:
    d = None if p.default is inspect._empty else p.default
    if isinstance(d, bool):
        t = "bool"
    elif isinstance(d, int):
        t = "int"
    elif isinstance(d, float):
        t = "float"
    else:
        t = "str"
    choices = CHOICES.get(p.name)
    return ParamSpec(p.name, "str" if choices else t, d, p.default is inspect._empty, choices=choices)


@dataclass
class PingouinSpec:
    kind: str
    fn: Callable
    data_style: bool
    array_args: list[str]          # array-style: column-name params passed positionally
    params: list[ParamSpec]
    inputs: list[PortSpec] = field(default_factory=lambda: [PortSpec("data", "frame")])
    outputs: list[PortSpec] = field(default_factory=lambda: [PortSpec("out", "frame")])
    category: str = "Stats"
    is_method: bool = False
    doc: str = ""

    def invoke(self, inbound: dict[str, list], params: dict) -> Any:
        pdf = inbound["data"][0].collect().to_pandas()
        def use(name: str) -> bool:  # skip blank/None params -> pingouin uses its own default
            v = params.get(name)
            return v is not None and v != ""
        if self.data_style:
            kw = {p.name: params[p.name] for p in self.params if use(p.name)}
            res = self.fn(data=pdf, **kw)
        else:
            arrs = [pdf[params[a]] for a in self.array_args if use(a)]
            kw = {p.name: params[p.name] for p in self.params if p.name not in self.array_args and use(p.name)}
            res = self.fn(*arrs, **kw)
        return _to_frame(res)

    def public(self) -> dict:
        return {
            "kind": self.kind, "category": self.category, "label": self.kind.split(".")[-1].replace("_", " "),
            "tier": "common", "doc": self.doc, "examples": "", "doc_url": None,
            "inputs": [vars(p) for p in self.inputs],
            "params": [{k: v for k, v in vars(p).items() if not (k == "choices" and v is None)} for p in self.params],
            "outputs": [vars(p) for p in self.outputs],
        }


def _build(fn: Callable, name: str) -> PingouinSpec | None:
    try:
        sig = inspect.signature(fn)
    except (ValueError, TypeError):
        return None
    ps = [p for p in sig.parameters.values() if p.kind not in (p.VAR_POSITIONAL, p.VAR_KEYWORD)]
    doc = (inspect.getdoc(fn) or "").split("\n\n")[0]

    if "data" in sig.parameters:                              # data-style: data -> frame port
        params = [_pspec(p) for p in ps if p.name != "data"]
        return PingouinSpec(f"pg.{name}", fn, True, [], params, doc=doc)

    if name in ARRAY_ARGS:                                     # array-style: arrays -> column-name params
        array_args = ARRAY_ARGS[name]
        params = [
            ParamSpec(a, "str", "", True, doc="column name") if a in array_args else _pspec(p)
            for a, p in [(p.name, p) for p in ps]
        ]
        return PingouinSpec(f"pg.{name}", fn, False, array_args, params, doc=doc)

    return None                                               # array-style without a translation -> skip


def reflect_pingouin() -> list[str]:
    """Register pingouin functions as Stats nodes. Returns the registered kinds (empty if pingouin absent)."""
    try:
        import pingouin as pg
    except ImportError:  # optional dependency
        loggr.info("pingouin not installed; skipping stats nodes")
        return []

    registered = []
    for name, fn in inspect.getmembers(pg, inspect.isfunction):
        if name.startswith(("_", "plot_")) or name in _SKIP:
            continue
        spec = _build(fn, name)
        if spec is not None:
            REGISTRY[spec.kind] = spec
            registered.append(spec.kind)
    return registered


# data-style helpers/plots/utilities that aren't useful graph nodes
_SKIP = {"read_dataset", "list_dataset", "print_table", "set_default_options", "remove_na",
         "multicomp", "convert_angles", "convert_effsize", "dichotomous_crosstab"}
