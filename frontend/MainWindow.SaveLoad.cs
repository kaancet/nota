using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stream = System.IO.Stream;             // avoid System.IO.Path clashing with the Avalonia Path shape
using StreamReader = System.IO.StreamReader;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;   // Popup
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;    // FuncDataTemplate
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;   // Bitmap (node icons)
using Avalonia.Platform;        // AssetLoader
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Nota;

public partial class MainWindow : Window
{
    // --- save / load a graph (.nota) ---

    // the on-disk shape: the frontend graph plus node positions (which the backend doc omits).
    private sealed record SavedNode(string id, string kind, double x, double y,
                                    Dictionary<string, object?> @params, bool collapsed = false);
    private sealed record SavedEdge(string sourceId, string sourceOut, string targetId, string targetIn);
    private sealed record SavedBox(double x, double y, double w, double h, string name, int colorIdx);
    // boxes is optional (added later) -> pre-box .nota files deserialize it to null
    private sealed record SavedGraph(int version, List<SavedNode> nodes, List<SavedEdge> edges,
                                     List<SavedBox>? boxes = null);

    private static readonly FilePickerFileType NotaFile = new("nota graph") { Patterns = new[] { "*.nota" } };
    private static readonly JsonSerializerOptions SaveOpts = new() { WriteIndented = true };

    private async void OnSave(object? sender, EventArgs e)   // NativeMenuItem.Click -> EventHandler
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save graph",
            DefaultExtension = "nota",
            SuggestedFileName = "graph.nota",
            FileTypeChoices = new[] { NotaFile },
        });
        if (file is null) return;   // cancelled

        var doc = new SavedGraph(1,
            _nodes.Select(n => new SavedNode(n.Id, n.Type.Kind, n.X, n.Y, n.Params, n.Collapsed)).ToList(),
            _edges.Select(ed => new SavedEdge(ed.SourceId, ed.SourceOut, ed.TargetId, ed.TargetIn)).ToList(),
            NodeCanvas.Children.OfType<Border>().Where(b => b.Tag is GroupBox)
                .Select(b => { var gb = (GroupBox)b.Tag!;
                               return new SavedBox(Canvas.GetLeft(b), Canvas.GetTop(b), b.Width, b.Height,
                                                   gb.Name.Text ?? "", gb.ColorIdx); }).ToList());
        try
        {
            await using Stream stream = await file.OpenWriteAsync();
            await JsonSerializer.SerializeAsync(stream, doc, SaveOpts);
            StatusText.Text = $"saved · {_nodes.Count} nodes → {file.Name}";
        }
        catch (Exception ex) { StatusText.Text = $"save error: {ex.Message}"; }
    }

    // Edit > Import Python Function as Node: pick a .py, register it in the backend, add to the palette
    private async void OnImportNode(object? sender, EventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Python function",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Python") { Patterns = new[] { "*.py" } } },
        });
        if (files.Count == 0) return;

        try
        {
            JsonElement? ir = BackendRequest("import_node", new { path = files[0].Path.LocalPath });
            if (ir is null) return;
            JsonElement resp = ir.Value;
            if (resp.TryGetProperty("error", out JsonElement err))
            {
                StatusText.Text = $"import error: {err.GetProperty("message").GetString()}";
                return;
            }
            NodeType t = ParseNodeType(resp.GetProperty("result"));
            _allTypes.RemoveAll(x => x.Kind == t.Kind);   // re-import replaces the old version
            _allTypes.Add(t);
            RebuildPalette(SearchBox.Text ?? "");
            StatusText.Text = $"imported {t.Label}";
        }
        catch (Exception ex) { StatusText.Text = $"import error: {ex.Message}"; }
    }

    private async void OnLoad(object? sender, EventArgs e)   // NativeMenuItem.Click -> EventHandler
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open graph", AllowMultiple = false, FileTypeFilter = new[] { NotaFile },
        });
        if (files.Count == 0) return;   // cancelled

        try
        {
            string json;
            await using (Stream stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream))
                json = await reader.ReadToEndAsync();
            LoadGraph(json);
        }
        catch (Exception ex) { StatusText.Text = $"load error: {ex.Message}"; }
    }

    // File > Load Example: open the picker in ~/.nota/examples (created if absent), then load like any .nota
    private async void OnLoadExample(object? sender, EventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nota", "examples");
        System.IO.Directory.CreateDirectory(dir);
        IStorageFolder? start = await top.StorageProvider.TryGetFolderFromPathAsync(dir);
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load example", AllowMultiple = false, FileTypeFilter = new[] { NotaFile },
            SuggestedStartLocation = start,
        });
        if (files.Count == 0) return;
        try
        {
            string json;
            await using (Stream stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream))
                json = await reader.ReadToEndAsync();
            LoadGraph(json);
        }
        catch (Exception ex) { StatusText.Text = $"load error: {ex.Message}"; }
    }

    // rebuild the canvas from a saved document: nodes at their positions, params restored, re-wired.
    private void LoadGraph(string json)
    {
        SavedGraph? doc = JsonSerializer.Deserialize<SavedGraph>(json);
        if (doc is null) { StatusText.Text = "load error: empty file"; return; }

        ClearGraph();
        var byId = new Dictionary<string, NodeInstance>();
        int skipped = 0;
        foreach (SavedNode sn in doc.nodes)
        {
            NodeType? type = _allTypes.FirstOrDefault(t => t.Kind == sn.kind);
            if (type is null) { skipped++; continue; }   // kind no longer in the manifest
            byId[sn.id] = AddNode(type, sn.x, sn.y, sn.id, sn.@params, sn.collapsed);
            BumpNextId(sn.id);
        }

        // dots only have real canvas positions after a layout pass -> force one before wiring
        NodeCanvas.UpdateLayout();
        foreach (SavedEdge se in doc.edges)
        {
            if (!byId.TryGetValue(se.sourceId, out NodeInstance? s) || !byId.TryGetValue(se.targetId, out NodeInstance? t))
                continue;
            Shape? from = FindDot(s, se.sourceOut, isInput: false);
            Shape? to = FindDot(t, se.targetIn, isInput: true);
            if (from is not null && to is not null) AddWire(from, to);
        }
        foreach (SavedBox sb in doc.boxes ?? Enumerable.Empty<SavedBox>())
        {
            AddBox(sb.x, sb.y, sb.w, sb.h, sb.name, sb.colorIdx);
            _boxColorIdx = Math.Max(_boxColorIdx, sb.colorIdx + 1);
        }
        StatusText.Text = skipped == 0
            ? $"loaded · {_nodes.Count} nodes"
            : $"loaded · {_nodes.Count} nodes ({skipped} unknown skipped)";
    }

    // wipe the canvas + graph state (used before a load)
    private void ClearGraph()
    {
        NodeCanvas.Children.Clear();
        InsertGrid();   // Children.Clear() removed the grid too -> put it back
        _nodes.Clear();
        _edges.Clear();
        _wires.Clear();
        _selNodes.Clear();
        _selWires.Clear();
        _selBoxes.Clear();
        _results = new Dictionary<string, JsonElement>();
        InfoArea.Content = new TextBlock { Text = "(click a node)", Foreground = ThemeSubtext };
        RefreshOutput();
    }

    // keep handed-out ids unique after a load: advance past any numeric id in the file
    private void BumpNextId(string id)
    {
        var digits = new string(id.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out int n) && n >= _nextId) _nextId = n + 1;
    }

}
