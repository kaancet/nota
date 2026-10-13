using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;   // ClipboardExtensions.TryGetValueAsync

namespace Nota;

public partial class MainWindow : Window
{
    // --- undo/redo (whole-graph snapshots) + copy/paste ---
    // ponytail: whole-graph snapshots; switch to per-action commands only if graphs exceed ~300 nodes.

    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private bool _restoring;                       // true while an undo/redo rebuild is in progress
    private const int HistoryCap = 50;
    private string? _preDrag;                       // graph state captured at drag start (node/box move)

    // the on-disk SavedGraph (nodes + positions + edges + boxes). Shared by OnSave and the history stack.
    private SavedGraph BuildSavedGraph() => new(1,
        _nodes.Select(n => new SavedNode(n.Id, n.Type.Kind, n.X, n.Y, n.Params, n.Collapsed)).ToList(),
        _edges.Select(ed => new SavedEdge(ed.SourceId, ed.SourceOut, ed.TargetId, ed.TargetIn)).ToList(),
        NodeCanvas.Children.OfType<Border>().Where(b => b.Tag is GroupBox or TextNote)
            .Select(b =>
            {
                if (b.Tag is TextNote tn)
                    return new SavedBox(Canvas.GetLeft(b), Canvas.GetTop(b), b.Width, b.Height, tn.Text.Text ?? "", 0, "text");
                var gb = (GroupBox)b.Tag!;
                return new SavedBox(Canvas.GetLeft(b), Canvas.GetTop(b), b.Width, b.Height, gb.Name.Text ?? "", gb.ColorIdx);
            }).ToList());

    private string SerializeGraph() => JsonSerializer.Serialize(BuildSavedGraph(), SaveOpts);

    // push a snapshot of the CURRENT state before a mutation; call this right before changing the graph
    private void Snapshot() => PushUndo(SerializeGraph());

    private void PushUndo(string snap)
    {
        if (_restoring) return;
        _undo.Add(snap);
        _redo.Clear();
        if (_undo.Count > HistoryCap) _undo.RemoveAt(0);
    }

    private void Undo()
    {
        if (_undo.Count == 0) { StatusText.Text = "nothing to undo"; return; }
        _redo.Add(SerializeGraph());
        string snap = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _restoring = true;
        LoadGraph(snap);
        _restoring = false;
    }

    private void Redo()
    {
        if (_redo.Count == 0) { StatusText.Text = "nothing to redo"; return; }
        _undo.Add(SerializeGraph());
        string snap = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _restoring = true;
        LoadGraph(snap);
        _restoring = false;
    }

    // a param text/code box: capture on focus, push one undo entry on blur only if the text changed
    // (so a whole edit session is a single undo, not one per keystroke)
    private void AddEditHistory(TextBox field) => AddEditHistory(field, () => field.Text ?? "");

    // general form: any control, reading its text through `getText` (used by the AutoCompleteBox in 4.5)
    private void AddEditHistory(Control field, Func<string> getText)
    {
        string? snap = null;
        string start = "";
        field.GotFocus += (_, _) => { snap = SerializeGraph(); start = getText(); };
        field.LostFocus += (_, _) =>
        {
            if (snap is not null && getText() != start) PushUndo(snap);
            snap = null;
        };
    }

    // Cmd on macOS, Ctrl elsewhere
    private static bool Cmd(KeyEventArgs e) =>
        e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    // --- copy / paste (clipboard text -> works across windows/instances) ---

    // an app-scoped clipboard format carrying our JSON (same DataFormat/DataTransfer API as the palette DnD)
    private static readonly DataFormat<string> ClipFormat = DataFormat.CreateStringApplicationFormat("nota.clip");

    private sealed record ClipDoc(int nota_clip, List<SavedNode> nodes, List<SavedEdge> edges);

    private async void OnCopy(object? sender, EventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is null || _selNodes.Count == 0) return;
        var ids = _selNodes.Select(b => b.Tag).OfType<NodeInstance>().Select(n => n.Id).ToHashSet();
        var nodes = _nodes.Where(n => ids.Contains(n.Id))
            .Select(n => new SavedNode(n.Id, n.Type.Kind, n.X, n.Y, n.Params, n.Collapsed)).ToList();
        var edges = _edges.Where(ed => ids.Contains(ed.SourceId) && ids.Contains(ed.TargetId))
            .Select(ed => new SavedEdge(ed.SourceId, ed.SourceOut, ed.TargetId, ed.TargetIn)).ToList();
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ClipFormat, JsonSerializer.Serialize(new ClipDoc(1, nodes, edges))));
        await top.Clipboard.SetDataAsync(data);
        StatusText.Text = $"copied {nodes.Count} node(s)";
    }

    private async void OnPaste(object? sender, EventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is null) return;
        string? text = await top.Clipboard.TryGetValueAsync(ClipFormat);
        if (string.IsNullOrWhiteSpace(text)) return;

        ClipDoc? clip;
        try { clip = JsonSerializer.Deserialize<ClipDoc>(text); }
        catch { return; }                                    // not our JSON -> ignore
        if (clip is null || clip.nota_clip != 1 || clip.nodes.Count == 0) return;

        Snapshot();
        var map = new Dictionary<string, NodeInstance>();    // old id -> new instance
        foreach (SavedNode sn in clip.nodes)
        {
            NodeType? type = _allTypes.FirstOrDefault(t => t.Kind == sn.kind);
            if (type is null) continue;                      // unknown kind -> skip
            string newId = $"n{_nextId++}•";
            map[sn.id] = AddNode(type, sn.x + 40, sn.y + 40, newId, sn.@params, sn.collapsed);
        }
        NodeCanvas.UpdateLayout();                           // dots need a layout pass before wiring
        foreach (SavedEdge se in clip.edges)
        {
            if (!map.TryGetValue(se.sourceId, out NodeInstance? s) || !map.TryGetValue(se.targetId, out NodeInstance? t))
                continue;
            Shape? from = FindDot(s, se.sourceOut, isInput: false);
            Shape? to = FindDot(t, se.targetIn, isInput: true);
            if (from is not null && to is not null) AddWire(from, to);
        }
        ClearSelection();                                    // select the pasted nodes
        foreach (NodeInstance n in map.Values)
            if (NodeBox(n) is Border box) SelectNode(box, additive: true);
        StatusText.Text = $"pasted {map.Count} node(s)";
    }
}
