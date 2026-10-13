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
    // --- run: serialize the graph, send it to the backend, show results ---

    // the graph-doc: nodes with their kind + inputs (edges live on the target). Shared by run/run-to-node.
    private object BuildGraphDoc() => new
    {
        nodes = _nodes.Select(n => new { id = n.Id, kind = n.Type.Kind, @params = n.Params, inputs = InputsFor(n) }).ToList(),
    };

    private string NodeLabel(string id) => _nodes.FirstOrDefault(n => n.Id == id)?.Type.Label ?? id;

    // Run (button + Cmd+R) and Run to Selected Node (Cmd+Shift+R). NativeMenuItem.Click hands an
    // EventHandler (EventArgs), the Button a RoutedEventHandler -> thin adapters onto the real logic.
    private void OnRun(object? sender, RoutedEventArgs e) => RunAll();
    private void OnRunMenu(object? sender, EventArgs e) => RunAll();

    private void RunAll()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            JsonElement? rr = BackendRequest("run_graph", new { graph = BuildGraphDoc() });
            if (rr is null) return;
            JsonElement resp = rr.Value;
            sw.Stop();
            if (resp.TryGetProperty("error", out JsonElement topErr))   // whole request failed (e.g. cycle)
            {
                StatusText.Text = $"run error: {topErr.GetProperty("message").GetString()}";
                return;
            }

            _results = new Dictionary<string, JsonElement>();
            int errors = 0;
            double slowestMs = -1; string slowest = "";
            foreach (JsonProperty node in resp.GetProperty("result").EnumerateObject())
            {
                _results[node.Name] = node.Value.Clone();
                if (node.Value.TryGetProperty("error", out _)) errors++;
                if (node.Value.TryGetProperty("ms", out JsonElement m) && m.ValueKind == JsonValueKind.Number
                    && m.GetDouble() > slowestMs) { slowestMs = m.GetDouble(); slowest = NodeLabel(node.Name); }
            }

            long total = sw.ElapsedMilliseconds;   // wall-clock for the whole run (backend ms is per-node compute)
            StatusText.Text = errors == 0
                ? $"ran ✓ · {_results.Count} nodes · {total} ms"
                : $"ran · {errors} error(s) · {total} ms" + (slowestMs >= 0 ? $" · slowest: {slowest} {slowestMs} ms" : "");
            RefreshHighlights();   // red/amber failed nodes
            RefreshOutput();       // update the output panel for whatever's selected
        }
        catch (Exception ex)
        {
            StatusText.Text = $"run error: {ex.Message}";
        }
    }

    // Cmd+Shift+R: run only the selected node and its ancestors (server prunes via preview).
    private void OnRunToNode(object? sender, EventArgs e)
    {
        NodeInstance? only = SoleNode();
        if (only is null) { StatusText.Text = "select one node to run to"; return; }
        RunToNode(only);
    }

    // run `only` and its ancestors via preview; shows just that node's result. Also used by data-file open.
    private void RunToNode(NodeInstance only)
    {
        try
        {
            JsonElement? rr = BackendRequest("preview", new { graph = BuildGraphDoc(), node_id = only.Id });
            if (rr is null) return;
            JsonElement resp = rr.Value;
            if (resp.TryGetProperty("error", out JsonElement topErr))
            {
                StatusText.Text = $"run error: {topErr.GetProperty("message").GetString()}";
                return;
            }
            JsonElement payload = resp.GetProperty("result");
            _results = new Dictionary<string, JsonElement> { [only.Id] = payload.Clone() };   // only this node; no stale others
            double ms = payload.TryGetProperty("ms", out JsonElement m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0;
            StatusText.Text = payload.TryGetProperty("error", out _)
                ? $"ran to {only.Type.Label}: error"
                : $"ran to {only.Type.Label} · {ms} ms";
            RefreshHighlights();
            RefreshOutput();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"run error: {ex.Message}";
        }
    }

    // target port -> list of [sourceId, sourceOut], built from the edges pointing at this node
    private object InputsFor(NodeInstance n)
    {
        var inputs = new Dictionary<string, List<string[]>>();
        foreach (Edge edge in _edges.Where(edge => edge.TargetId == n.Id))
        {
            if (!inputs.TryGetValue(edge.TargetIn, out List<string[]>? list))
                inputs[edge.TargetIn] = list = new List<string[]>();
            list.Add(new[] { edge.SourceId, edge.SourceOut });
        }
        return inputs;
    }

}
