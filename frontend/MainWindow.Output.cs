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
    // --- output panel rendering ---

    private void RefreshOutput()
    {
        string? id = SoleNode()?.Id;
        OutputArea.Content = id is not null && _results.TryGetValue(id, out JsonElement payload)
            ? BuildOutput(payload)
            : new TextBlock { Text = "(Run the graph, then click a node)", Foreground = ThemeSubtext };
    }

    private static Control BuildOutput(JsonElement p)
    {
        Control body;
        if (p.TryGetProperty("error", out JsonElement err))
            body = Scroll(new TextBlock { Text = err.GetString(), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });
        else
            body = p.GetProperty("type").GetString() switch
            {
                "frame" => BuildTable(p),   // a DataGrid -> brings its own virtualized scrolling
                "expr" => Scroll(new TextBlock { Text = p.GetProperty("repr").GetString(), Foreground = ThemeText, TextWrapping = TextWrapping.Wrap }),
                "html" => Scroll(BuildPlotOutput(p)),
                _ => Scroll(new TextBlock { Text = CellText(p.GetProperty("value")), Foreground = ThemeText, TextWrapping = TextWrapping.Wrap }),
            };

        // wrap body with a header line (shape/ms) on top and the node's captured log on the bottom (1.6)
        var dock = new DockPanel { LastChildFill = true };
        Control? header = BuildHeader(p);
        if (header is not null) { DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header); }
        if (p.TryGetProperty("log", out JsonElement logEl) && logEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(logEl.GetString()))
        {
            Control log = BuildLog(logEl.GetString()!);
            DockPanel.SetDock(log, Dock.Bottom);
            dock.Children.Add(log);
        }
        dock.Children.Add(body);
        return dock;
    }

    // one grey line: "N rows × M cols · first 50 shown · +K more columns hidden · 1.6 ms"
    private static Control? BuildHeader(JsonElement p)
    {
        var parts = new List<string>();
        if (p.TryGetProperty("type", out JsonElement t) && t.GetString() == "frame" && p.TryGetProperty("shape", out JsonElement shp))
        {
            JsonElement[] dims = shp.EnumerateArray().ToArray();
            string rows = dims[0].ValueKind == JsonValueKind.Null ? "?" : dims[0].GetInt32().ToString();
            int cols = dims[1].GetInt32();
            parts.Add($"{rows} rows × {cols} cols");
            if (p.TryGetProperty("truncated", out JsonElement tr) && tr.ValueKind == JsonValueKind.True) parts.Add("first 50 shown");
            int shown = p.GetProperty("columns").GetArrayLength();
            if (cols - shown > 0) parts.Add($"+{cols - shown} more columns hidden");
        }
        if (p.TryGetProperty("ms", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number) parts.Add($"{ms.GetDouble()} ms");
        return parts.Count == 0 ? null
            : new TextBlock { Text = string.Join(" · ", parts), Foreground = ThemeSubtext, FontSize = 11, Margin = new Thickness(2, 0, 0, 4) };
    }

    // the node's captured stdout + warnings, monospace, in a short scroller docked below the output
    private static Control BuildLog(string text) => new ScrollViewer
    {
        MaxHeight = 90,
        Margin = new Thickness(0, 4, 0, 0),
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "log", Foreground = ThemeSubtext, FontSize = 10, Margin = new Thickness(2, 0, 0, 1) },
                new TextBlock { Text = text, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    FontSize = 11, Foreground = ThemeText, TextWrapping = TextWrapping.Wrap },
            },
        },
    };

    // wrap non-table output so long content scrolls (OutputArea is a bare ContentControl now)
    private static Control Scroll(Control c) => new ScrollViewer
    {
        Content = c,
        Padding = new Thickness(2),
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    // a plot payload: the plot opens from the "Open plot ↗" button on the node itself
    private static Control BuildPlotOutput(JsonElement p) => new TextBlock
    {
        Text = "Interactive plot ready — use “Open plot ↗” on the node.",
        Foreground = ThemeText,
        TextWrapping = TextWrapping.Wrap,
    };

    // open a plot node's last-run figure in the browser (or nudge the user to run first)
    private void OpenPlotFor(NodeInstance node)
    {
        if (_results.TryGetValue(node.Id, out JsonElement p)
            && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("html", out JsonElement h) && h.ValueKind == JsonValueKind.String)
            OpenHtml(h.GetString()!);
        else
            StatusText.Text = "run the graph first";
    }

    // write the standalone HTML to a temp file and open it in the default browser (a pop-out window)
    private static void OpenHtml(string html)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nota_plot_{System.Guid.NewGuid():N}.html");
        System.IO.File.WriteAllText(path, html);
        OpenUrl(path);
    }

    // a virtualized DataGrid (rows recycled) instead of a hand-built Grid -> wide/tall frames stay fast.
    // Rows are string[] (cell display text); each column binds to its index.
    private static Control BuildTable(JsonElement p)
    {
        var cols = p.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        var rows = new List<string[]>();
        foreach (JsonElement r in p.GetProperty("rows").EnumerateArray())
            rows.Add(r.EnumerateArray().Select(CellText).ToArray());

        var dg = new DataGrid
        {
            ItemsSource = rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            CanUserResizeColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MaxColumnWidth = 320,
            FontSize = 12,
        };
        // column header = "name  dtype" (dtype from the payload schema); falls back to the bare name.
        bool hasSchema = p.TryGetProperty("schema", out JsonElement schema);
        for (int i = 0; i < cols.Count; i++)
        {
            string head = cols[i];
            if (hasSchema && schema.TryGetProperty(cols[i], out JsonElement dt) && dt.ValueKind == JsonValueKind.String)
                head = $"{cols[i]}  {dt.GetString()}";
            dg.Columns.Add(new DataGridTextColumn { Header = head, Binding = new Binding($"[{i}]") });
        }
        return dg;   // row/col counts + "N hidden" now live in the output header (BuildHeader)
    }

}
