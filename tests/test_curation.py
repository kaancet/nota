"""Palette curation: tiers, categories, hiding (nodes stay runnable)."""

from helpers import BUILTIN_KINDS  # noqa: F401  (ensures nota.nodes loaded)

from nota.core import REGISTRY, catalog, manifest


def test_common_is_small_and_labeled():
    common = manifest("common")
    assert 30 < len(common) < 120
    for e in common:
        assert e["tier"] == "common" and e["label"] and e["category"]
    filt = next(e for e in common if e["kind"] == "LazyFrame.filter")
    assert (filt["category"], filt["label"]) == ("Transform", "Filter")


def test_eager_families_hidden_but_runnable():
    shown = {e["kind"] for e in manifest("all")}
    assert not any(k.startswith(("DataFrame.", "Series.")) for k in shown)
    assert "DataFrame.to_pandas" not in shown
    assert "DataFrame.filter" in REGISTRY        # hidden from palette, still executable


def test_catalog_groups_by_category():
    tree = catalog("common")
    assert "Transform" in tree and "Sink" in tree
    assert any(e["kind"] == "sink.preview" for e in tree["Sink"])


def test_manifest_enrichment():
    m = {e["kind"]: e for e in manifest("common")}
    # #5 examples + doc_url derived from the reflected member
    f = m["LazyFrame.filter"]
    assert f["doc_url"].endswith("polars.LazyFrame.filter.html")
    assert f["examples"]  # docstring Examples section captured
    # #4 per-arg blurb parsed from the docstring's Parameters section
    assert "boolean" in next(p for p in f["inputs"] if p["name"] == "predicates")["doc"].lower()
    # #3 enum choices reflected from the param's Literal annotation -> a dropdown in the UI
    how = next(p for p in m["LazyFrame.join"]["params"] if p["name"] == "how")
    assert "inner" in how["choices"] and "cross" in how["choices"]
    mode = next(p for p in m["Expr.round"]["params"] if p["name"] == "mode")
    assert mode["choices"] == ["half_to_even", "half_away_from_zero"]   # RoundMode = Literal[...]
    # custom nodes have no polars doc URL
    assert m["sink.preview"]["doc_url"] is None
