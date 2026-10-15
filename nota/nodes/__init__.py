"""Built-in node library (@node functions). Importing this package registers them.

Kept separate from nota.core so the core stays a dependency-free engine; a frontend
or the server does `import nota.nodes` to load the builtins into the REGISTRY.
"""

from . import code, columns, dicts, literals, plots, simple, sinks, sources, stats  # noqa: F401  (import triggers @node registration)
from .pingouin_bridge import reflect_pingouin

reflect_pingouin()  # register pingouin stats nodes (no-op if pingouin isn't installed)

__all__ = [
    "code",
    "columns",
    "dicts",
    "literals",
    "plots",
    "simple",
    "sinks",
    "sources",
    "stats",
    "reflect_pingouin",
]
