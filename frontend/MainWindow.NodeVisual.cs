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
    // --- dragging + resizing a node ---

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border box) return;
        // pressing inside a param field -> let it take focus/edit; don't drag or steal focus
        if (e.Source is Control c && c.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
        if (e.Source is Control c2 && c2.FindAncestorOfType<ComboBox>(includeSelf: true) is not null) return;
        if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;   // right button = pan (tunnel)
        NodeCanvas.Focus();   // grabbing the node body commits + deselects any open param field
        Point local = e.GetPosition(box);
        bool right = local.X >= box.Bounds.Width - ResizeEdge;
        bool bottom = local.Y >= box.Bounds.Height - ResizeEdge;

        if (right || bottom)   // grabbed an edge/corner -> resize
        {
            _resizing = box;
            _resizeRight = right;
            _resizeBottom = bottom;
            _resizeStartPos = e.GetPosition(NodeCanvas);
            _resizeStartW = box.Bounds.Width;
            _resizeStartH = box.Bounds.Height;

            // measure the content unconstrained -> the smallest size that still fits everything (+ border)
            box.Child?.Measure(Size.Infinity);
            _natW = (box.Child?.DesiredSize.Width ?? 0) + 2;
            _natH = (box.Child?.DesiredSize.Height ?? 0) + 2;

            e.Pointer.Capture(box);
            return;
        }

        // otherwise -> drag. grabbing an unselected node (no shift) makes it the sole selection,
        // so multi-move below includes it; grabbing one already in a multi-selection moves the group.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !_selNodes.Contains(box))
            SelectNode(box, additive: false);
        _dragging = box;
        _nodeMoved = false;                 // reset; a press with no move is a click (select)
        _grabOffset = e.GetPosition(box);   // pointer position WITHIN the node
        e.Pointer.Capture(box);
    }

    private void OnNodeMoved(object? sender, PointerEventArgs e)
    {
        // resizing in progress
        if (_resizing is not null)
        {
            Point rp = e.GetPosition(NodeCanvas);
            double minW = Math.Max(ResizeMinW, _natW);   // never below the content's natural width
            double minH = Math.Max(ResizeMinH, _natH);
            if (_resizeRight) _resizing.Width = Math.Max(minW, _resizeStartW + (rp.X - _resizeStartPos.X));
            if (_resizeBottom) _resizing.Height = Math.Max(minH, _resizeStartH + (rp.Y - _resizeStartPos.Y));
            RedrawWiresLive();   // ports move as the node grows -> redraw from live positions
            return;
        }

        // not dragging: give edge-hover cursor feedback
        if (_dragging is null)
        {
            if (sender is Border h)
            {
                Point l = e.GetPosition(h);
                bool r = l.X >= h.Bounds.Width - ResizeEdge;
                bool b = l.Y >= h.Bounds.Height - ResizeEdge;
                h.Cursor = r && b ? new Cursor(StandardCursorType.BottomRightCorner)
                         : r ? new Cursor(StandardCursorType.SizeWestEast)
                         : b ? new Cursor(StandardCursorType.SizeNorthSouth)
                         : Cursor.Default;
            }
            return;
        }

        // dragging: move the whole selection by the same delta (no clamp -> content may pass the viewport, #10)
        _nodeMoved = true;
        Point p = e.GetPosition(NodeCanvas);
        var dn = (NodeInstance)_dragging.Tag!;
        double dx = (p.X - _grabOffset.X) - dn.X;
        double dy = (p.Y - _grabOffset.Y) - dn.Y;

        IEnumerable<Border> moving = _selNodes.Contains(_dragging) ? _selNodes : new[] { _dragging };
        foreach (Border b in moving)
        {
            var n = (NodeInstance)b.Tag!;
            n.X += dx;
            n.Y += dy;
            Canvas.SetLeft(b, n.X);
            Canvas.SetTop(b, n.Y);
        }
        UpdateWires();
    }

    private void OnNodeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizing is not null)
        {
            RecomputeOffsets(_resizing);   // ports settled at new positions -> refresh cached offsets
            UpdateWires();
            _resizing = null;
            e.Pointer.Capture(null);
            return;
        }

        if (!_nodeMoved && _dragging is not null)
            SelectNode(_dragging, e.KeyModifiers.HasFlag(KeyModifiers.Shift));   // click (no drag) selects
        _dragging = null;
        e.Pointer.Capture(null);
    }

    // redraw every wire from its dots' live positions (used mid-resize; may lag a frame, snaps on release)
    private void RedrawWiresLive()
    {
        foreach (Wire w in _wires)
            w.Path.Data = BezierGeometry(DotCenter(w.From), DotCenter(w.To));
    }

    // after a resize, recompute the cached offsets for wires touching this node
    private void RecomputeOffsets(Border box)
    {
        if (box.Tag is not NodeInstance node) return;
        foreach (Wire w in _wires)
        {
            if (DotNode(w.From) == node) w.FromOffset = LocalOffset(w.From);
            if (DotNode(w.To) == node) w.ToOffset = LocalOffset(w.To);
        }
    }

    // --- building a node's visual: header on top, input ports left, output ports right ---

    private Border BuildNodeVisual(NodeInstance node)
    {
        Border box = null!;                       // assigned at the end; captured by the collapse toggle
        var detail = new List<Control>();         // params + port labels -> hidden when collapsed (dots stay)

        // chevron toggles the node body; ▾ = expanded, ▸ = collapsed
        var chevron = new TextBlock
        {
            Text = "▾",
            Foreground = Brushes.White,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        void ToggleCollapse()
        {
            node.Collapsed = !node.Collapsed;
            chevron.Text = node.Collapsed ? "▸" : "▾";
            foreach (Control c in detail) c.IsVisible = !node.Collapsed;
            NodeCanvas.UpdateLayout();             // ports settle at new positions before we cache them
            RecomputeOffsets(box);
            UpdateWires();
        }
        chevron.PointerPressed += (_, ev) => { ev.Handled = true; ToggleCollapse(); };   // don't start a node drag

        // colored title bar: label (fills) + chevron (right)
        var titleGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleText = new TextBlock
        {
            Text = node.Type.Label,
            Foreground = Brushes.White,
            FontWeight = FontWeight.DemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(chevron, 1);
        titleGrid.Children.Add(titleText);
        titleGrid.Children.Add(chevron);
        var titleBar = new Border
        {
            Background = CategoryBrush(node.Type.Category),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding = new Thickness(10, 4),
            Child = titleGrid,
        };

        // input rows (left), output rows (right); each row's name label is a collapsible detail
        var inputs = new StackPanel { Spacing = 4 };
        foreach (Port p in node.Type.Inputs)
            inputs.Children.Add(PortRow(node, p, isInput: true, detail));

        var outputs = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,   // hug the right edge of its column
            Margin = new Thickness(24, 0, 0, 0),               // minimum gap from the inputs
        };
        foreach (Port p in node.Type.Outputs)
            outputs.Children.Add(PortRow(node, p, isInput: false, detail));

        // Auto column = inputs (left); star column takes the rest so outputs pin to the node's right edge
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 6, 0, 6),
        };
        Grid.SetColumn(inputs, 0);
        Grid.SetColumn(outputs, 1);
        body.Children.Add(inputs);
        body.Children.Add(outputs);

        var content = new StackPanel();
        content.Children.Add(titleBar);
        Action refreshLayout = () => { NodeCanvas.UpdateLayout(); RecomputeOffsets(box); UpdateWires(); };
        Control? paramsPanel = BuildParams(node, refreshLayout);
        if (paramsPanel is not null) { content.Children.Add(paramsPanel); detail.Add(paramsPanel); }
        content.Children.Add(body);

        // plot nodes carry an "Open plot ↗" button that pops the last run's figure into a browser
        if (node.Type.Kind.StartsWith("sink.plot", StringComparison.Ordinal))
        {
            var plotBtn = new Button
            {
                Content = "Open plot ↗",
                FontSize = 11,
                Margin = new Thickness(10, 0, 10, 8),
                Padding = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            plotBtn.Click += (_, _) => OpenPlotFor(node);   // default Button styling -> theme-legible text
            content.Children.Add(plotBtn);
            detail.Add(plotBtn);   // hidden when the node is collapsed, like the other body widgets
        }

        box = new Border
        {
            Background = ThemeNodeBg,
            BorderBrush = Brushes.SteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),        // no padding: title bar + dots reach the edges
            ClipToBounds = false,              // let the port dots overflow past the border
            MinWidth = ResizeMinW,             // never so narrow that labels/params overlap
            Tag = node,                        // visual -> model link
            Child = content,
        };
        if (node.Collapsed) { chevron.Text = "▸"; foreach (Control c in detail) c.IsVisible = false; }  // restore saved state
        return box;
    }

    // --- param widgets on the node body ---

    private Control? BuildParams(NodeInstance node, Action? refreshLayout = null)
    {
        // skip params that are ALSO input ports (the "port-or-literal" ones) -> no double UI
        var portNames = node.Type.Inputs.Select(i => i.Name).ToHashSet();
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(10, 4, 10, 2) };
        foreach (ParamSpec p in node.Type.Params)
            if (!portNames.Contains(p.Name))
                panel.Children.Add(ParamRow(node, p, refreshLayout));
        return panel.Children.Count == 0 ? null : panel;
    }

    private static Control ParamRow(NodeInstance node, ParamSpec p, Action? refreshLayout = null)
    {
        if (p.Widget == "kvlist") return BuildKvList(node, p, refreshLayout);   // dynamic key/value row editor (dict.build)
        if (p.Type == "bool") return BoolWidget(node, p);   // checkbox carries its own label

        // one compact row: grey label on the left, a slim flat field on the right
        var label = new TextBlock
        {
            Text = p.Name,
            FontSize = 11,
            Foreground = ThemeSubtext,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MaxWidth = 92,                                     // a long label truncates, never widens the node
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(label, DocTip(p.Doc, p.Name));         // hover shows the param's blurb (or its name)

        // an enum param (curated `choices`) -> a dropdown instead of a free-text field (#3)
        if (p.Choices is not null)
        {
            var combo = ChoiceWidget(node, p);
            var crow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(combo, 1);
            crow.Children.Add(label);
            crow.Children.Add(combo);
            return crow;
        }

        TextBox field = CompactBox(node.Params[p.Name]?.ToString() ?? "");
        field.TextChanged += (_, _) => node.Params[p.Name] = Coerce(field.Text ?? "", p.Type);

        // a "path" param gets a Browse… button that opens the OS file picker
        bool isPath = p.Name == "path";
        if (isPath) field.MaxWidth = 300;   // a long path scrolls inside the field, doesn't stretch the node
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(isPath ? "Auto,*,Auto" : "Auto,*") };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(field, 1);
        row.Children.Add(label);
        row.Children.Add(field);
        if (isPath)
        {
            var browse = new Button
            {
                Content = "…", FontSize = 11, Height = 22, MinHeight = 0,
                Padding = new Thickness(6, 0), Margin = new Thickness(4, 0, 0, 0),
            };
            browse.Click += (_, _) => BrowseForPath(field);   // fire the async picker
            Grid.SetColumn(browse, 2);
            row.Children.Add(browse);
        }
        return row;
    }

    // open the OS file picker and drop the chosen path into the field (async: don't freeze the UI)
    private static async void BrowseForPath(TextBox field)
    {
        TopLevel? top = TopLevel.GetTopLevel(field);   // the window this field lives in
        if (top is null) return;

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Select a file", AllowMultiple = false });

        if (files.Count > 0)
            field.Text = files[0].Path.LocalPath;   // setting Text fires TextChanged -> updates node.Params
    }

    // a slim, flat, borderless input field (Blender-ish)
    private static TextBox CompactBox(string text) => new TextBox
    {
        Text = text,
        FontSize = 11,
        MinHeight = 0,
        MinWidth = 0,      // override Fluent's large default so the field yields to the label
        Height = 22,
        Padding = new Thickness(6, 1),
        Background = ThemeField,
        Foreground = ThemeText,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static Control BoolWidget(NodeInstance node, ParamSpec p)
    {
        var cb = new CheckBox
        {
            Content = p.Name,
            IsChecked = node.Params[p.Name] as bool? ?? false,
            FontSize = 11,
            MinHeight = 0,
            Padding = new Thickness(4, 0, 0, 0),
        };
        cb.IsCheckedChanged += (_, _) => node.Params[p.Name] = cb.IsChecked;
        return cb;
    }

    // enum param -> a dropdown of its curated choices (#3)
    private static Control ChoiceWidget(NodeInstance node, ParamSpec p)
    {
        var combo = new ComboBox
        {
            ItemsSource = p.Choices,
            SelectedItem = node.Params[p.Name]?.ToString(),
            FontSize = 11,
            MinHeight = 0,
            Height = 24,
            Padding = new Thickness(6, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        combo.SelectionChanged += (_, _) => node.Params[p.Name] = combo.SelectedItem as string;
        return combo;
    }

    // a param/port's blurb for a tooltip, falling back to its name when undocumented
    private static string DocTip(string doc, string fallback) => string.IsNullOrWhiteSpace(doc) ? fallback : doc;

    // --- kvlist widget: a dynamic key/value/type row editor (dict.build's `entries`) ---

    private static readonly string[] KvTypes = { "auto", "str", "int", "float", "bool", "json" };

    private static Control BuildKvList(NodeInstance node, ParamSpec p, Action? refreshLayout = null)
    {
        var rowsPanel = new StackPanel { Spacing = 3 };
        var rows = new List<(TextBox Key, TextBox Val, ComboBox Type)>();

        void Commit()
        {
            var entries = new List<object?>();
            foreach ((TextBox k, TextBox v, ComboBox t) in rows)
            {
                string type = t.SelectedItem as string ?? "auto";
                entries.Add(new List<object?> { k.Text ?? "", CoerceKv(v.Text ?? "", type), type });
            }
            node.Params[p.Name] = entries;   // list of [key, typed value, type] -> sent as-is / saved
        }

        void AddRow(string key, string val, string type)
        {
            TextBox k = CompactBox(key); k.Width = 74;
            TextBox v = CompactBox(val);
            var ty = new ComboBox
            {
                ItemsSource = KvTypes,
                SelectedItem = KvTypes.Contains(type) ? type : "auto",
                FontSize = 11, MinHeight = 0, Height = 22, Padding = new Thickness(4, 0),
            };
            var del = new Button { Content = "✕", FontSize = 10, Height = 22, MinHeight = 0, Padding = new Thickness(5, 0) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 3 };
            Grid.SetColumn(k, 0); Grid.SetColumn(v, 1); Grid.SetColumn(ty, 2); Grid.SetColumn(del, 3);
            row.Children.Add(k); row.Children.Add(v); row.Children.Add(ty); row.Children.Add(del);

            var entry = (k, v, ty);
            rows.Add(entry);
            k.TextChanged += (_, _) => Commit();
            v.TextChanged += (_, _) => Commit();
            ty.SelectionChanged += (_, _) => Commit();
            del.Click += (_, _) => { rows.Remove(entry); rowsPanel.Children.Remove(row); Commit(); refreshLayout?.Invoke(); };
            rowsPanel.Children.Add(row);
        }

        // seed existing rows (e.g. from a loaded .nota file): entries = [[key, value, type], ...]
        if (node.Params.TryGetValue(p.Name, out object? existing) && existing is IEnumerable<object?> saved)
            foreach (object? r in saved)
                if (r is IReadOnlyList<object?> cells && cells.Count >= 1)
                {
                    string type = cells.Count >= 3 ? cells[2]?.ToString() ?? "auto" : InferKvType(cells.Count >= 2 ? cells[1] : null);
                    AddRow(cells[0]?.ToString() ?? "", ToKvText(cells.Count >= 2 ? cells[1] : null, type), type);
                }

        var add = new Button
        {
            Content = "+ add row", FontSize = 11, Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.Click += (_, _) => { AddRow("", "", "auto"); refreshLayout?.Invoke(); };

        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(10, 4, 10, 4) };
        panel.Children.Add(new TextBlock { Text = p.Name, FontSize = 11, Foreground = ThemeSubtext });
        panel.Children.Add(rowsPanel);
        panel.Children.Add(add);
        return panel;
    }

    // one kvlist cell's text -> a typed value (matches the row's type dropdown)
    private static object? CoerceKv(string text, string type) => type switch
    {
        "str" => text,
        "int" => long.TryParse(text, out long l) ? l : (object?)null,
        "float" => double.TryParse(text, out double d) ? d : (object?)null,
        "bool" => bool.TryParse(text, out bool b) ? b : (object?)null,
        "json" => TryJson(text),
        _ => ParseLoose(text),   // "auto"
    };

    private static object? TryJson(string s)
    {
        try { using var doc = JsonDocument.Parse(s); return JsonToValue(doc.RootElement); }
        catch { return s; }   // invalid JSON -> keep the raw text
    }

    // a loaded typed value -> the text to show in the cell
    private static string ToKvText(object? v, string type) => v is null ? ""
        : type == "json" ? JsonSerializer.Serialize(v)
        : v.ToString() ?? "";

    private static string InferKvType(object? v) => v switch
    {
        long or int => "int",
        double or float => "float",
        bool => "bool",
        string => "str",
        _ => "auto",
    };

    // text -> a value of the param's type (empty/invalid -> null)
    private static object? Coerce(string s, string type) => type switch
    {
        "int" => long.TryParse(s, out long l) ? l : (object?)null,
        "float" => double.TryParse(s, out double d) ? d : (object?)null,
        "any" => ParseLoose(s),
        _ => s,   // str
    };

    // "any"-typed text: try to read it as a number/bool, else keep the string
    private static object? ParseLoose(string s)
    {
        if (long.TryParse(s, out long l)) return l;
        if (double.TryParse(s, out double d)) return d;
        if (bool.TryParse(s, out bool b)) return b;
        return s;
    }

    // category -> title-bar color (foundation for the color-coding idea; palette coloring is TBD)
    private static IBrush CategoryBrush(string category) => category switch
    {
        "Source" => Brush("#2E7D32"),
        "Sink" => Brush("#6A1B9A"),
        "Transform" => Brush("#1565C0"),
        "Aggregate" => Brush("#EF6C00"),
        "Column" => Brush("#00838F"),
        "Literal" => Brush("#546E7A"),
        "Expression" => Brush("#5E35B1"),
        "Compare" => Brush("#AD1457"),
        "Logic" => Brush("#C62828"),
        "String" => Brush("#00695C"),
        "Date" => Brush("#4527A0"),
        "List" => Brush("#37474F"),
        "Dict" => Brush("#6D4C41"),
        "Plot" => Brush("#3949AB"),
        "Imported" => Brush("#00897B"),
        "User generated" => Brush("#7B1FA2"),
        "Stats" => Brush("#00796B"),
        "Pinned" => Brush("#C2185B"),
        "Recent" => Brush("#607D8B"),
        _ => Brush("#455A64"),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    // --- theme: brush instances stay fixed; SyncTheme() recolors them from the App resources on
    // theme change, so every control using them repaints. Keys live in App.axaml ThemeDictionaries. ---
    private static readonly SolidColorBrush ThemeNodeBg = new(Color.Parse("#2D2D30"));
    private static readonly SolidColorBrush ThemeField = new(Color.Parse("#3A3A3D"));
    private static readonly SolidColorBrush ThemeText = new(Color.Parse("#DCDCDC"));
    private static readonly SolidColorBrush ThemeSubtext = new(Color.Parse("#9AA0A6"));
    private static readonly SolidColorBrush ThemeBorder = new(Color.Parse("#3C3C40"));
    private static readonly SolidColorBrush ThemeGrid = new(Color.Parse("#2A2A2E"));
    private static readonly SolidColorBrush ThemeTableHeader = new(Color.Parse("#333338"));
    private static readonly SolidColorBrush ThemeTableZebra = new(Color.Parse("#2A2A2E"));
    private static readonly SolidColorBrush ThemeAccent = new(Color.Parse("#4C8DFF"));

    private static void SyncTheme()
    {
        Application? app = Application.Current;
        if (app is null) return;
        ThemeVariant v = app.ActualThemeVariant;
        void S(SolidColorBrush b, string key)
        {
            if (app.TryGetResource(key, v, out object? r) && r is SolidColorBrush s) b.Color = s.Color;
        }
        S(ThemeNodeBg, "NotaNodeBg"); S(ThemeField, "NotaField"); S(ThemeText, "NotaText");
        S(ThemeSubtext, "NotaSubtext"); S(ThemeBorder, "NotaBorder"); S(ThemeGrid, "NotaGrid");
        S(ThemeTableHeader, "NotaTableHeader"); S(ThemeTableZebra, "NotaTableZebra"); S(ThemeAccent, "NotaAccent");
    }

    private void OnThemeDark(object? sender, EventArgs e) => SetTheme(ThemeVariant.Dark);
    private void OnThemeLight(object? sender, EventArgs e) => SetTheme(ThemeVariant.Light);
    private void OnThemeSystem(object? sender, EventArgs e) => SetTheme(ThemeVariant.Default);

    private void SetTheme(ThemeVariant v)
    {
        if (Application.Current is not null) Application.Current.RequestedThemeVariant = v;   // -> ActualThemeVariantChanged -> SyncTheme
        UpdateThemeChecks(v);
    }

    // the View > Theme items (located by header; x:Name doesn't generate fields inside NativeMenu)
    private NativeMenuItem? _darkItem, _lightItem, _systemItem;

    private void CacheThemeItems()
    {
        foreach (NativeMenuItem it in AllMenuItems(NativeMenu.GetMenu(this)))
            switch (it.Header)
            {
                case "Dark": _darkItem = it; break;
                case "Light": _lightItem = it; break;
                case "System": _systemItem = it; break;
            }
    }

    private static IEnumerable<NativeMenuItem> AllMenuItems(NativeMenu? menu)
    {
        if (menu is null) yield break;
        foreach (var e in menu.Items)
            if (e is NativeMenuItem it)
            {
                yield return it;
                foreach (NativeMenuItem c in AllMenuItems(it.Menu)) yield return c;
            }
    }

    // check exactly the active theme in View > Theme (Default == System)
    private void UpdateThemeChecks(ThemeVariant v)
    {
        if (_darkItem is not null) _darkItem.IsChecked = v == ThemeVariant.Dark;
        if (_lightItem is not null) _lightItem.IsChecked = v == ThemeVariant.Light;
        if (_systemItem is not null) _systemItem.IsChecked = v == ThemeVariant.Default;
    }

    // port fill color by the data type it carries/expects
    private static IBrush TypeColor(string type) => type switch
    {
        "frame" => Brush("#26A69A"),   // teal
        "expr" => Brush("#AB47BC"),    // purple
        "series" => Brush("#FFB300"),  // amber
        "scalar" => Brush("#EF5350"),  // red
        "figure" => Brush("#42A5F5"), // blue
        _ => Brush("#90A4AE"),         // any / unknown = grey
    };

    // one port: a small dot + its name. Input = dot then name (left); output = name then dot (right).
    // The name label is added to `collapsibles` so a collapsed node can hide it while the dot stays.
    private Control PortRow(NodeInstance node, Port port, bool isInput, List<Control>? collapsibles = null)
    {
        // define the port dot shape
        // required port = circle, optional port = square; color = the data type it carries
        Shape dot = port.Optional ? new Rectangle() : new Ellipse();
        dot.Width = 10;
        dot.Height = 10;
        dot.Fill = TypeColor(port.Type);
        dot.Stroke = Brush("#1E1E1E");     // thin dark edge normally
        dot.StrokeThickness = 1;
        dot.VerticalAlignment = VerticalAlignment.Center;
        // poke halfway past the box edge: input dot leftwards, output dot rightwards
        dot.Margin = isInput ? new Thickness(-5, 0, 0, 0) : new Thickness(0, 0, -5, 0);
        
        dot.Tag = new PortRef(node, port, isInput);   // the dot knows which port it is
        dot.PointerPressed += OnPortPressed;          // pressing a dot starts a wire, NOT a node drag
        dot.PointerMoved += OnPortMoved;              // while wiring: move the rubber-band
        dot.PointerReleased += OnPortReleased;        // release: try to complete the wire
        dot.PointerEntered += OnPortEnter;            // hover highlight
        dot.PointerExited += OnPortExit;
        var name = new TextBlock
        {
            Text = PortLabel(port),
            Foreground = ThemeText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(dot, DocTip(port.Doc, port.Name));   // hover a port dot -> its blurb (#4)
        collapsibles?.Add(name);                            // hidden when the node collapses (#6)

        // display-only label: show the receiver ("self") by its type, keep other names as-is.
        // The real port name (Port.Name) is unchanged -- that's what connect() uses.
        static string PortLabel(Port p) => p.Name == "self" ? TypeLabel(p.Type) : p.Name;
        static string TypeLabel(string type) => type switch
        {
            "frame" => "DataFrame",
            "expr" => "Expr",
            "series" => "Series",
            _ => type,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (isInput)
        {
            row.Children.Add(dot);
            row.Children.Add(name);
        }
        else
        {
            row.Children.Add(name);
            row.Children.Add(dot);
            row.HorizontalAlignment = HorizontalAlignment.Right;
        }
        return row;
    }

    private Window? _legendWindow;

    private void OnShowPortLegend(object? sender, EventArgs e)
    {
        if (_legendWindow is not null) { _legendWindow.Activate(); return; }

        var entries = new (string type, string color, string label)[]
        {
            ("frame",  "#26A69A", "Frame (DataFrame / LazyFrame)"),
            ("expr",   "#AB47BC", "Expression"),
            ("series", "#FFB300", "Series"),
            ("scalar", "#EF5350", "Scalar"),
            ("figure", "#42A5F5", "Figure (plot overlay)"),
            ("any",    "#90A4AE", "Any / Unknown"),
        };

        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions(), ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
        int row = 0;
        // header: shapes
        var shapeNote = new TextBlock
        {
            Text = "● = required     ■ = optional", FontSize = 12,
            Foreground = ThemeSubtext, Margin = new Thickness(0, 0, 0, 12),
        };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(shapeNote, row); Grid.SetColumnSpan(shapeNote, 4);
        grid.Children.Add(shapeNote);
        row++;

        foreach (var (type, color, label) in entries)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var circle = new Ellipse { Width = 12, Height = 12, Fill = Brush(color), Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(circle, row); Grid.SetColumn(circle, 0);
            grid.Children.Add(circle);

            var square = new Rectangle { Width = 12, Height = 12, Fill = Brush(color), Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(square, row); Grid.SetColumn(square, 1);
            grid.Children.Add(square);

            var typeTb = new TextBlock { Text = type, FontWeight = FontWeight.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 12, 2) };
            Grid.SetRow(typeTb, row); Grid.SetColumn(typeTb, 2);
            grid.Children.Add(typeTb);

            var labelTb = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeSubtext, Margin = new Thickness(0, 2) };
            Grid.SetRow(labelTb, row); Grid.SetColumn(labelTb, 3);
            grid.Children.Add(labelTb);

            row++;
        }

        _legendWindow = new Window
        {
            Title = "Port Legend", Width = 380, Height = 40 + row * 28, SizeToContent = SizeToContent.Height,
            Content = grid, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _legendWindow.Closed += (_, _) => _legendWindow = null;
        _legendWindow.Show(this);
    }

}
