"""Built-in node library (@node functions). Importing this package registers them.

Kept separate from nota.core so the core stays a dependency-free engine; a frontend
or the server does `import nota.nodes` to load the builtins into the REGISTRY.
"""

from . import columns, literals, sinks, sources  # noqa: F401  (import triggers @node registration)

__all__ = ["columns", "literals", "sinks", "sources"]
