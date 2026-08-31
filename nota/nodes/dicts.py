"""Dict builder node -- assemble a dict from key/value rows edited in the UI.

Feeds the kwargs/`overrides` ports of other nodes (e.g. plot styling) without typing JSON.
`entries` is a list of ``[key, value, type?]`` rows (values already typed by the frontend);
`base` is an optional dict input port to extend/merge onto.
"""

from __future__ import annotations

from nota.core.registry import node


@node("dict.build", "Dict")
def build_dict(base: object = None, *, entries=None) -> object:
    """Build a dict from key/value rows. Wire the output into any kwargs input (e.g. a plot's `overrides`)."""
    out = dict(base or {})
    for row in entries or []:
        if row and row[0] not in (None, ""):  # skip blank rows
            out[row[0]] = row[1] if len(row) > 1 else None
    return out
