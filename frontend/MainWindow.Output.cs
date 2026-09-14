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
        if (p.TryGetProperty("error", out JsonElement err))
            return Scroll(new TextBlock { Text = err.GetString(), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });

        return p.GetProperty("type").GetString() switch
        {
            "frame" => BuildTable(p),   // a DataGrid -> brings its own virtualized scrolling
            "expr" => Scroll(new TextBlock { Text = p.GetProperty("repr").GetString(), Foreground = ThemeText, TextWrapping = TextWrapping.Wrap }),
            "html" => Scroll(BuildPlotOutput(p)),
            _ => Scroll(new TextBlock { Text = CellText(p.GetProperty("value")), Foreground = ThemeText, TextWrapping = TextWrapping.Wrap }),
        };
    }

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
        for (int i = 0; i < cols.Count; i++)
            dg.Columns.Add(new DataGridTextColumn { Header = cols[i], Binding = new Binding($"[{i}]") });

        // shape[1] is the TRUE column count; if the backend capped the payload, say how many are hidden
        int total = p.GetProperty("shape").EnumerateArray().Last().GetInt32();
        int hidden = total - cols.Count;
        if (hidden <= 0) return dg;

        var note = new TextBlock
        {
            Text = $"+{hidden} more columns hidden",
            Foreground = ThemeSubtext, FontSize = 11, Margin = new Thickness(4, 2, 0, 3),
        };
        var panel = new DockPanel();
        DockPanel.SetDock(note, Dock.Top);
        panel.Children.Add(note);
        panel.Children.Add(dg);
        return panel;
    }

}
