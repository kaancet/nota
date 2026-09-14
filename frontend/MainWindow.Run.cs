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

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            // build the graph-doc: nodes with their kind + inputs (edges live on the target)
            var graphDoc = new
            {
                nodes = _nodes.Select(n => new { id = n.Id, kind = n.Type.Kind, @params = n.Params, inputs = InputsFor(n) }).ToList(),
            };

            JsonElement? rr = BackendRequest("run_graph", new { graph = graphDoc });
            if (rr is null) return;
            JsonElement resp = rr.Value;
            if (resp.TryGetProperty("error", out JsonElement topErr))   // whole request failed (e.g. cycle)
            {
                StatusText.Text = $"run error: {topErr.GetProperty("message").GetString()}";
                return;
            }

            _results = new Dictionary<string, JsonElement>();
            int errors = 0;
            foreach (JsonProperty node in resp.GetProperty("result").EnumerateObject())
            {
                _results[node.Name] = node.Value.Clone();
                if (node.Value.TryGetProperty("error", out _)) errors++;
            }

            StatusText.Text = errors == 0 ? $"ran ✓ · {_results.Count} nodes" : $"ran · {errors} error(s)";
            RefreshOutput();   // update the output panel for whatever's selected
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
