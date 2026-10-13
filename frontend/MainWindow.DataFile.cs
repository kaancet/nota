using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Platform.Storage;

namespace Nota;

public partial class MainWindow : Window
{
    // --- data-first entry: a data file becomes Source + Profile, already run ---
    // keep in sync with nota/nodes/sources.py _BY_EXT
    private static readonly string[] DataExts =
        { ".csv", ".tsv", ".parquet", ".pq", ".json", ".ndjson", ".arrow", ".ipc", ".feather", ".xlsx", ".xls" };
    private static readonly FilePickerFileType DataFileType =
        new("data file") { Patterns = DataExts.Select(x => "*" + x).ToArray() };

    // File > Open Data File… (Cmd+Shift+O): drops the node pair near the top-left of the visible canvas
    private async void OnOpenDataFile(object? sender, EventArgs e)
    {
        TopLevel? top = GetTopLevel(this);
        if (top is null) return;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Open data file", AllowMultiple = true, FileTypeFilter = new[] { DataFileType } });
        if (files.Count == 0) return;
        Point at = CanvasViewport.TranslatePoint(new Point(40, 40), NodeCanvas) ?? new Point(40, 40);
        OpenDataFiles(files.Select(f => f.Path.LocalPath), at.X, at.Y);
    }

    // one Source+Profile pair per file, stacked 160px apart; the last profile is selected and run
    private void OpenDataFiles(IEnumerable<string> paths, double x, double y)
    {
        List<string> list = paths.ToList();
        if (list.Count == 1 && list[0].EndsWith(".nota", StringComparison.OrdinalIgnoreCase))
        {
            LoadGraph(System.IO.File.ReadAllText(list[0]));   // a dropped graph just loads
            return;
        }
        NodeType? src = _allTypes.FirstOrDefault(t => t.Kind == "source.auto");
        NodeType? prof = _allTypes.FirstOrDefault(t => t.Kind == "stats.profile");
        if (src is null || prof is null) { StatusText.Text = "backend lacks source.auto / stats.profile"; return; }

        var ok = list.Where(p => DataExts.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant())).ToList();
        foreach (string bad in list.Except(ok))
            StatusText.Text = $"unsupported file type: {System.IO.Path.GetExtension(bad)}";
        if (ok.Count == 0) return;

        Snapshot();   // one undo step for the whole drop
        NodeInstance? lastProfile = null;
        var pairs = new List<(NodeInstance s, NodeInstance p)>();
        for (int i = 0; i < ok.Count; i++)
        {
            double yy = y + i * 160;
            NodeInstance s = AddNode(src, x, yy, $"n{_nextId++}•", new() { ["path"] = ok[i] });
            NodeInstance p = AddNode(prof, x + 320, yy, $"n{_nextId++}•");
            pairs.Add((s, p));
            lastProfile = p;
        }
        NodeCanvas.UpdateLayout();   // dots only have positions after a layout pass
        foreach ((NodeInstance s, NodeInstance p) in pairs)
        {
            Shape? from = FindDot(s, "out", isInput: false);
            Shape? to = FindDot(p, "frame", isInput: true);
            if (from is not null && to is not null) AddWire(from, to);
        }

        Border? box = NodeCanvas.Children.OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.Tag, lastProfile));
        if (box is not null) SelectNode(box, additive: false);
        RunToNode(lastProfile!);
    }
}
