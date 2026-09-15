"""CLI runner: .nota (frontend format) -> convert -> run -> per-node summary + exit code."""

import json

from nota.cli import main, nota_to_graph_doc


def _write(path, nodes, edges):
    path.write_text(json.dumps({"version": 1, "nodes": nodes, "edges": edges}), encoding="utf-8")


def test_nota_to_graph_doc_builds_inputs():
    doc = {"nodes": [{"id": "a", "kind": "k"}, {"id": "b", "kind": "j"}],
           "edges": [{"sourceId": "a", "sourceOut": "out", "targetId": "b", "targetIn": "frame"}]}
    g = nota_to_graph_doc(doc)
    assert g["nodes"][1]["inputs"] == {"frame": [["a", "out"]]}


def test_nota_to_graph_doc_passthrough():
    doc = {"nodes": [{"id": "a", "kind": "k", "inputs": {}}]}   # already a graph doc
    assert nota_to_graph_doc(doc) is doc


def test_cli_run_json(tmp_path, capsys):
    p = tmp_path / "g.nota"
    _write(p, [{"id": "src", "kind": "source.sample", "params": {}},
               {"id": "pv", "kind": "sink.preview", "params": {}}],
           [{"sourceId": "src", "sourceOut": "out", "targetId": "pv", "targetIn": "frame"}])
    rc = main(["run", str(p), "--json"])
    out = json.loads(capsys.readouterr().out)
    assert out["pv"]["rows"] and rc == 0


def test_cli_run_bad_source_exits_1(tmp_path, capsys):
    p = tmp_path / "bad.nota"
    _write(p, [{"id": "src", "kind": "source.csv", "params": {"path": "/nonexistent_cli.csv"}},
               {"id": "pv", "kind": "sink.preview", "params": {}}],
           [{"sourceId": "src", "sourceOut": "out", "targetId": "pv", "targetIn": "frame"}])
    assert main(["run", str(p)]) == 1
