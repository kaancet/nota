from .registry import REGISTRY, node, manifest, catalog, reflect_polars, import_function
from .graph import Graph, run

# populate REGISTRY with the whole polars surface on import
reflect_polars()

# composite (macro) nodes -- importing registers the io.* boundary placeholders
from .composite import create_node, load_user_nodes  # noqa: E402

__all__ = ["REGISTRY", "node", "manifest", "catalog", "reflect_polars", "import_function",
           "Graph", "run", "create_node", "load_user_nodes"]
