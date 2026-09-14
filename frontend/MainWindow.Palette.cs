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
    // --- palette: search (#1) + category sections (#2) ---

    // categories the user has collapsed (remembered across rebuilds); empty = all expanded
    private readonly HashSet<string> _collapsed = new();

    private bool _showAll;                               // Edit > Nodes > Show All Nodes (tier=all vs common)
    private readonly List<string> _recent = new();      // recently added kinds, most-recent first
    private readonly HashSet<string> _pinned = new();   // pinned kinds (shown in a top section)
    private const int RecentMax = 6;
    private Point _lastCanvasWorld;                      // cursor in canvas/world space -> Shift+A placement
    private Popup? _quickAdd;                            // Shift+A quick-add popup
    private string? _qaCategory;                         // quick-add: current category (null = category list)

    private sealed record PalettePrefs(List<string> recent, List<string> pinned);

    private static string PalettePrefsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nota", "palette.json");

    private void LoadPalettePrefs()
    {
        try
        {
            if (!System.IO.File.Exists(PalettePrefsPath)) return;
            PalettePrefs? p = JsonSerializer.Deserialize<PalettePrefs>(System.IO.File.ReadAllText(PalettePrefsPath));
            if (p is null) return;
            _recent.AddRange(p.recent ?? new List<string>());
            foreach (string k in p.pinned ?? new List<string>()) _pinned.Add(k);
        }
        catch { /* corrupt/missing prefs -> start empty */ }
    }

    private void SavePalettePrefs()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PalettePrefsPath)!);
            System.IO.File.WriteAllText(PalettePrefsPath, JsonSerializer.Serialize(new PalettePrefs(_recent, _pinned.ToList())));
        }
        catch { /* best-effort persistence */ }
    }

    private void PushRecent(string kind)
    {
        _recent.Remove(kind);
        _recent.Insert(0, kind);
        if (_recent.Count > RecentMax) _recent.RemoveRange(RecentMax, _recent.Count - RecentMax);
        SavePalettePrefs();
        RebuildPalette(SearchBox.Text ?? "");
    }

    private void TogglePin(string kind)
    {
        if (!_pinned.Remove(kind)) _pinned.Add(kind);
        SavePalettePrefs();
        RebuildPalette(SearchBox.Text ?? "");
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RebuildPalette(SearchBox.Text ?? "");

    // rebuild the palette: filter by label/kind, group by category into collapsible sections,
    // each section an Nx3 grid of node cards (icon + name, category-colored border).
    private void RebuildPalette(string filter)
    {
        PaletteList.Children.Clear();
        string f = filter.Trim();
        bool searching = f.Length > 0;

        if (!searching)   // Pinned + Recent sections at the top (hidden while filtering)
        {
            List<NodeType> pinned = _pinned.Select(FindType).OfType<NodeType>().ToList();
            if (pinned.Count > 0) AddPaletteSection("Pinned", pinned, false);
            List<NodeType> recent = _recent.Select(FindType).OfType<NodeType>()
                                           .Where(t => !_pinned.Contains(t.Kind)).ToList();
            if (recent.Count > 0) AddPaletteSection("Recent", recent, false);
        }

        // category sections respect the tier toggle; search always spans every tier
        IEnumerable<NodeType> pool = _showAll || searching ? _allTypes : _allTypes.Where(t => t.Tier == "common");
        IEnumerable<NodeType> matched = searching
            ? pool.Where(t => t.Label.Contains(f, StringComparison.OrdinalIgnoreCase)
                           || t.Kind.Contains(f, StringComparison.OrdinalIgnoreCase))
            : pool;

        foreach (IGrouping<string, NodeType> g in matched.GroupBy(t => t.Category))
            AddPaletteSection(g.Key, g.ToList(), searching);
    }

    private NodeType? FindType(string kind) => _allTypes.FirstOrDefault(t => t.Kind == kind);

    // one collapsible palette section: a header (chevron + swatch + count) over an Nx-card WrapPanel.
    private void AddPaletteSection(string title, List<NodeType> items, bool forceOpen)
    {
        bool open = forceOpen || !_collapsed.Contains(title);
        var cards = new WrapPanel   // fixed-size cards; WrapPanel reflows columns to width, capped at 10
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 10 * CardSlot, IsVisible = open,
        };
        foreach (NodeType t in items) cards.Children.Add(BuildNodeCard(t));

        var chevron = new TextBlock { Text = open ? "▾" : "▸", FontSize = 10, Width = 12,
                                      VerticalAlignment = VerticalAlignment.Center };
        var header = new Border
        {
            Padding = new Thickness(2, 5, 2, 3), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { chevron, CategoryHeader(title, items.Count) },
            },
        };
        header.PointerPressed += (_, _) =>
        {
            if (forceOpen) return;                       // don't toggle remembered state while filtering
            bool now = !cards.IsVisible;
            cards.IsVisible = now;
            chevron.Text = now ? "▾" : "▸";
            if (now) _collapsed.Remove(title); else _collapsed.Add(title);
        };
        PaletteList.Children.Add(header);
        PaletteList.Children.Add(cards);
    }

    private const double CardWidth = 72;                 // fixed card width -> WrapPanel tiles to fit
    private const double CardSlot = CardWidth + 6;       // + left/right margin (3 each)

    // one palette node card: placeholder icon centered, label below, border = category color.
    // Double-tap adds it (cascade); press-drag drops it at the cursor (HookPaletteDrag).
    private Control BuildNodeCard(NodeType t)
    {
        var card = new Border
        {
            Tag = t,
            Width = CardWidth,
            BorderBrush = CategoryBrush(t.Category),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Background = ThemeNodeBg,
            Padding = new Thickness(4, 6, 4, 6),
            Margin = new Thickness(3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid   // content + a pin toggle overlaid top-right
            {
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            BuildNodeIcon(t),
                            new TextBlock
                            {
                                Text = t.Label, FontSize = 10.5, Foreground = ThemeText,
                                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                                TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                        },
                    },
                    BuildPin(t),
                },
            },
        };
        ToolTip.SetTip(card, $"{t.Label}\n{t.Kind}");
        card.Tapped += (_, e) => { if (e.Source is not Button) InfoArea.Content = BuildInfo(t); };   // single click -> info
        card.DoubleTapped += (_, e) => { if (e.Source is not Button) AddNodeCascade(t); };
        HookPaletteDrag(card, t);
        return card;
    }

    // pushpin toggle in the card corner (Material "push_pin"): filled = pinned, faint = not.
    private static readonly Geometry _pinGeometry = Geometry.Parse(
        "M16 9V4h1c.55 0 1-.45 1-1s-.45-1-1-1H7c-.55 0-1 .45-1 1s.45 1 1 1h1v5c0 1.66-1.34 3-3 3v2h5.97v7l1 1 1-1v-7H19v-2c-1.66 0-3-1.34-3-3z");

    private Control BuildPin(NodeType t)
    {
        bool pinned = _pinned.Contains(t.Kind);
        var btn = new Button
        {
            Width = 16, Height = 16, Padding = new Thickness(0),
            Margin = new Thickness(0, -4, -3, 0),   // pull into the card's padding -> hug the corner
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Path
            {
                Data = _pinGeometry, Stretch = Stretch.Uniform, Width = 12, Height = 12,
                Fill = pinned ? ThemeAccent : ThemeSubtext, Opacity = pinned ? 1.0 : 0.35,
            },
        };
        ToolTip.SetTip(btn, pinned ? "Unpin" : "Pin");
        btn.Click += (_, e) => { e.Handled = true; TogglePin(t.Kind); };
        return btn;
    }

    // node icon: an image from Assets/icons/<kind>.png if one exists, else a colored initial tile.
    // Drop a PNG named after the node kind (e.g. "LazyFrame.filter.png") to give a node an icon.
    private static readonly Dictionary<string, Bitmap?> _iconCache = new();

    private static Bitmap? LoadIcon(string kind)
    {
        if (_iconCache.TryGetValue(kind, out Bitmap? cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            var uri = new Uri($"avares://Nota/Assets/icons/{kind}.png");
            if (AssetLoader.Exists(uri)) bmp = new Bitmap(AssetLoader.Open(uri));
        }
        catch { bmp = null; }   // bad/missing asset -> fall back to the tile
        _iconCache[kind] = bmp;
        return bmp;
    }

    private static Control BuildNodeIcon(NodeType t, double size = 30)
    {
        Bitmap? icon = LoadIcon(t.Kind);
        if (icon is not null)
            return new Image { Source = icon, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };

        return new Border   // placeholder: category-colored rounded tile with the label's initial
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 5),
            Background = CategoryBrush(t.Category), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = t.Label.Length > 0 ? t.Label[..1].ToUpperInvariant() : "?",
                FontSize = size / 2, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private void AddNodeCascade(NodeType t)
    {
        double offset = 40 + (_nodes.Count % 12) * 26;   // cascade so drops don't stack
        AddNode(t, offset, offset, $"n{_nextId++}•");
        PushRecent(t.Kind);
    }

    // a color-coded category row: a swatch (its category color) + name + node count
    private static Control CategoryHeader(string category, int count) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 7,
        Children =
        {
            new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = CategoryBrush(category) },
            // no explicit Foreground -> inherits the theme text color (readable in light + dark)
            new TextBlock { Text = category, FontWeight = FontWeight.SemiBold, FontSize = 12 },
            new TextBlock { Text = $"({count})", FontSize = 11, Opacity = 0.6 },   // dimmed but theme-aware
        },
    };

    // turn one manifest JSON entry into a NodeType
    private static NodeType ParseNodeType(JsonElement node) => new NodeType(
        Kind: node.GetProperty("kind").GetString()!,
        Label: node.GetProperty("label").GetString()!,
        Category: node.GetProperty("category").GetString()!,
        Tier: node.GetProperty("tier").GetString()!,
        Doc: node.GetProperty("doc").GetString() ?? "",
        Inputs: ParsePorts(node.GetProperty("inputs")),
        Outputs: ParsePorts(node.GetProperty("outputs")),
        Params: ParseParams(node.GetProperty("params")),
        Examples: Opt(node, "examples")?.GetString() ?? "",
        DocUrl: Opt(node, "doc_url")?.GetString());

    // a property if present and non-null, else null (protocol 1.1 fields are optional)
    private static JsonElement? Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v : null;

    // turn a "params" JSON array into a list of ParamSpec
    private static List<ParamSpec> ParseParams(JsonElement array)
    {
        var ps = new List<ParamSpec>();
        foreach (JsonElement p in array.EnumerateArray())
            ps.Add(new ParamSpec(
                p.GetProperty("name").GetString()!,
                p.GetProperty("type").GetString()!,
                JsonToValue(p.GetProperty("default")),
                p.GetProperty("required").GetBoolean(),
                Opt(p, "doc")?.GetString() ?? "",
                Opt(p, "choices") is JsonElement c
                    ? c.EnumerateArray().Select(x => x.GetString()!).ToList()
                    : null,
                Opt(p, "widget")?.GetString()));
        return ps;
    }

    // one JSON value -> a plain C# value (string / long / double / bool / null)
    private static object? JsonToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => e.EnumerateArray().Select(JsonToValue).ToList(),   // e.g. kvlist entries
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(pr => pr.Name, pr => JsonToValue(pr.Value)),
        _ => e.GetRawText(),
    };

    // turn an "inputs"/"outputs" JSON array into a list of Port objects
    private static List<Port> ParsePorts(JsonElement array)
    {
        var ports = new List<Port>();
        foreach (JsonElement p in array.EnumerateArray())
            ports.Add(new Port(
                p.GetProperty("name").GetString()!,
                p.GetProperty("type").GetString()!,
                p.GetProperty("variadic").GetBoolean(),
                p.GetProperty("optional").GetBoolean(),
                Opt(p, "doc")?.GetString() ?? ""));
        return ports;
    }

    // let a palette card start a drag. Threshold-gated so a plain click/double-tap still
    // adds via the card's DoubleTapped; only a press-then-move begins a DoDragDrop.
    private void HookPaletteDrag(Control leaf, NodeType t)
    {
        leaf.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Button) { _paletteDragCandidate = null; return; }   // pin button -> not a drag
            _paletteDragCandidate = t;
            _paletteDragStart = e.GetPosition(this);
            _paletteDragPress = e;             // DoDragDropAsync needs the press args; start once moved
        }, RoutingStrategies.Tunnel);

        leaf.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (_paletteDragActive || _paletteDragPress is null || !ReferenceEquals(_paletteDragCandidate, t)) return;
            if (!e.GetCurrentPoint(leaf).Properties.IsLeftButtonPressed) { _paletteDragCandidate = null; _paletteDragPress = null; return; }
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _paletteDragStart.X) < 4 && Math.Abs(p.Y - _paletteDragStart.Y) < 4) return;   // threshold

            _paletteDragActive = true;
            ShowGhost(t, e.GetPosition(OverlayCanvas));
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(NodeKindFormat, t.Kind));
            try { await DragDrop.DoDragDropAsync(_paletteDragPress, data, DragDropEffects.Copy); }   // blocks until drop/cancel
            finally { HideGhost(); _paletteDragActive = false; _paletteDragCandidate = null; _paletteDragPress = null; }
        }, RoutingStrategies.Tunnel);
    }

    // a small floating preview of the node, mirroring its palette row, shown while dragging
    private void ShowGhost(NodeType t, Point at)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4, 8, 4),
            Children =
            {
                new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2),
                             Background = CategoryBrush(t.Category), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = t.Label, FontSize = 12, Foreground = ThemeText, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _dragGhost = new Border
        {
            Background = ThemeNodeBg, BorderBrush = CategoryBrush(t.Category), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Opacity = 0.85, IsHitTestVisible = false, Child = content,
        };
        OverlayCanvas.Children.Add(_dragGhost);
        MoveGhost(at);
    }

    private void MoveGhost(Point at)
    {
        if (_dragGhost is null) return;
        Canvas.SetLeft(_dragGhost, at.X + 12);   // offset off the cursor so it doesn't sit under it
        Canvas.SetTop(_dragGhost, at.Y + 8);
    }

    private void HideGhost()
    {
        if (_dragGhost is not null) { OverlayCanvas.Children.Remove(_dragGhost); _dragGhost = null; }
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (_paletteDragActive) MoveGhost(e.GetPosition(OverlayCanvas));
    }

    private void OnCanvasDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(NodeKindFormat) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(NodeKindFormat) is not string kind) return;
        NodeType? type = _allTypes.FirstOrDefault(x => x.Kind == kind);
        if (type is null) return;
        Point w = e.GetPosition(NodeCanvas);   // world space (through zoom/pan), like a marquee anchor
        AddNode(type, w.X, w.Y, $"n{_nextId++}•");
        PushRecent(type.Kind);
    }

    // create a placed node: model + visual + canvas placement + drag hookup. Shared by
    // palette drops and graph load. initParams (from a .nota file) override the type defaults.
    private NodeInstance AddNode(NodeType type, double x, double y, string id,
                                 Dictionary<string, object?>? initParams = null, bool collapsed = false)
    {
        var instance = new NodeInstance(id, type, x, y) { Collapsed = collapsed };
        if (initParams is not null)
            foreach (KeyValuePair<string, object?> kv in initParams)
                if (instance.Params.ContainsKey(kv.Key))   // widgets read these when the visual is built below
                    instance.Params[kv.Key] = kv.Value is JsonElement je ? JsonToValue(je) : kv.Value;
        _nodes.Add(instance);

        var box = BuildNodeVisual(instance);
        box.PointerPressed += OnNodePressed;
        box.PointerMoved += OnNodeMoved;
        box.PointerReleased += OnNodeReleased;

        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        NodeCanvas.Children.Add(box);
        return instance;
    }

}
