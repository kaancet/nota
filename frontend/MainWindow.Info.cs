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
    // --- node info panel (right) ---

    private Control BuildInfo(NodeType t)
    {
        var root = new StackPanel { Spacing = 6 };

        // header: label, kind, category/tier badge
        root.Children.Add(new TextBlock { Text = t.Label, FontWeight = FontWeight.Bold, FontSize = 16, Foreground = ThemeText });
        root.Children.Add(new TextBlock { Text = t.Kind, FontSize = 11, Foreground = ThemeSubtext });
        root.Children.Add(new Border
        {
            Background = CategoryBrush(t.Category),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = $"{t.Category} · {t.Tier}", FontSize = 10, Foreground = Brushes.White },
        });

        // description
        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(t.Doc) ? "(no description)" : t.Doc,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeText,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        });

        if (t.Inputs.Count > 0) { root.Children.Add(Section("Inputs")); foreach (Port p in t.Inputs) root.Children.Add(PortLine(p)); }
        if (t.Outputs.Count > 0) { root.Children.Add(Section("Outputs")); foreach (Port p in t.Outputs) root.Children.Add(PortLine(p)); }
        if (t.Params.Count > 0) { root.Children.Add(Section("Parameters")); foreach (ParamSpec p in t.Params) root.Children.Add(ParamLine(p)); }

        // examples (#5): the docstring's Examples section, monospaced
        if (!string.IsNullOrWhiteSpace(t.Examples))
        {
            root.Children.Add(Section("Examples"));
            root.Children.Add(new Border
            {
                Background = ThemeField,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4),
                Child = new TextBlock
                {
                    Text = t.Examples,
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    FontSize = 11,
                    Foreground = ThemeText,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        // doc link (#5): opens the polars API page in the browser
        if (!string.IsNullOrWhiteSpace(t.DocUrl))
        {
            var link = new TextBlock
            {
                Text = "polars docs ↗",
                Foreground = Brush("#6BA5E7"),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                TextDecorations = TextDecorations.Underline,
            };
            link.PointerPressed += (_, _) => OpenUrl(t.DocUrl!);
            root.Children.Add(link);
        }

        return root;
    }

    // open a URL in the OS default browser
    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / bad url -> ignore */ }
    }

    private static Control Section(string title) => new TextBlock
    {
        Text = title, FontWeight = FontWeight.Bold, FontSize = 12, Foreground = ThemeText, Margin = new Thickness(0, 8, 0, 2),
    };

    private static Control Chip(string text, IBrush bg) => new Border
    {
        Background = bg, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.White },
    };

    private static Control PortLine(Port p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = TypeColor(p.Type), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = p.Name == "self" ? p.Type : p.Name, Foreground = ThemeText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Chip(p.Type, TypeColor(p.Type)));
        row.Children.Add(Chip(p.Optional ? "optional" : "required", p.Optional ? Brush("#5A5A5A") : Brush("#9C4A4A")));
        if (p.Variadic) row.Children.Add(Chip("many", Brush("#7A7A44")));
        return WithDoc(row, p.Doc);   // per-port blurb underneath (#4)
    }

    private static Control ParamLine(ParamSpec p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(new TextBlock { Text = p.Name, Foreground = ThemeText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Chip(p.Type, Brush("#4A5A7A")));
        row.Children.Add(Chip(p.Required ? "required" : $"= {p.Default ?? "null"}", p.Required ? Brush("#9C4A4A") : Brush("#4A4A4A")));
        if (p.Choices is not null) row.Children.Add(Chip($"{p.Choices.Count} choices", Brush("#3A6A3A")));
        return WithDoc(row, p.Doc);   // per-param blurb underneath (#4)
    }

    // stack an optional grey blurb under a header row; returns the row unchanged if there's no doc
    private static Control WithDoc(Control header, string doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return header;
        var stack = new StackPanel { Margin = new Thickness(0, 1, 0, 3) };
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock
        {
            Text = doc,
            Foreground = ThemeSubtext,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(15, 0, 0, 0),
        });
        return stack;
    }

    // a JSON cell as display text (unquoted for strings, raw JSON otherwise)
    private static string CellText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();

    // runs when the window closes -> shut the Python backend down (no stray processes)
}
