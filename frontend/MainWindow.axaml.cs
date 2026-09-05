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
    // one Backend for the app's lifetime -> Python starts once and stays running
    private readonly Backend _backend;

    // the full node catalog from the manifest (unfiltered) -> the palette filters/groups this
    private List<NodeType> _allTypes = new();

    // palette drag-to-canvas state (Avalonia 12 DnD: DataTransfer + typed DataFormat)
    private static readonly DataFormat<string> NodeKindFormat =
        DataFormat.CreateStringApplicationFormat("nota.nodekind");
    private NodeType? _paletteDragCandidate;              // row pressed, may become a drag
    private Point _paletteDragStart;                      // press point (screen) -> threshold
    private PointerPressedEventArgs? _paletteDragPress;   // the press that DoDragDropAsync needs
    private bool _paletteDragActive;                      // a DoDragDropAsync loop is running
    private Control? _dragGhost;                          // floating preview following the cursor

    // the frontend's copy of the graph: every node dropped on the canvas
    private readonly List<NodeInstance> _nodes = new();
    private int _nextId;      // used to hand out unique ids: n0, n1, n2, ...

    private Border? _dragging;   // the node currently being dragged (null = none)
    private Point _grabOffset;   // where inside the node the pointer grabbed it
    private bool _nodeMoved;     // did the current press actually drag, or was it a click?

    // node resizing state
    private const double ResizeEdge = 8, ResizeMinW = 150, ResizeMinH = 44;
    private Border? _resizing;
    private bool _resizeRight, _resizeBottom;
    private Point _resizeStartPos;
    private double _resizeStartW, _resizeStartH;
    private double _natW, _natH;   // node's natural content size -> the smallest it may shrink to

    // current selection (sets -> marquee/shift can select many) -> Delete removes all
    private readonly HashSet<Border> _selNodes = new();
    private readonly HashSet<Wire> _selWires = new();

    // canvas zoom/pan (#10): NodeCanvas carries a Scale+Translate render transform
    private readonly ScaleTransform _zoom = new();
    private readonly TranslateTransform _pan = new();
    private bool _panning;          // middle-drag pans the viewport
    private Point _panStart;        // pointer screen pos where the pan began
    private Point _panOrigin;       // translate values when the pan began

    // marquee (#9): left-drag on empty canvas selects intersecting nodes
    private Rectangle? _marquee;
    private Point _marqueeStart;    // world-space anchor of the rubber rect
    private bool _marqueeing;

    // group boxes: right-drag on the canvas draws a colored region (behind nodes/wires) for
    // organizing big graphs. Purely visual -> selectable/movable/deletable, never affects nodes.
    private sealed class GroupBox { public int ColorIdx; public TextBox Name = null!; public Button Swatch = null!; }
    private static readonly Color[] _boxColors =
    {
        Color.FromRgb(0x42, 0x85, 0xF4), Color.FromRgb(0x0F, 0x9D, 0x58),
        Color.FromRgb(0xF4, 0xB4, 0x00), Color.FromRgb(0xDB, 0x44, 0x37),
        Color.FromRgb(0xAB, 0x47, 0xBC), Color.FromRgb(0x00, 0x89, 0x7B),
    };
    private readonly HashSet<Border> _selBoxes = new();
    private Border? _drawingBox;    // box being rubber-banded by the current right-drag
    private Point _boxStart;        // world anchor of that draw
    private Border? _draggingBox;   // box being moved
    private Point _boxDragStart;    // world pos where a box move began
    private Point _boxDragOrigin;   // box (left,top) when the move began
    private int _boxColorIdx;       // cycles the default color for each new box
    private static IBrush BoxFill(Color c) => new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B));

    // wire-dragging state (null when not dragging a wire)
    private PortRef? _wireSource;      // the output port we started from
    private Shape? _wireSourceDot;   // its dot (for the wire's start point)
    private Path? _rubberBand;         // the bezier wire following the cursor
    private Point _wireStart;          // fixed source-dot center for the duration of a wire drag
    private Shape? _wireHoverDot;      // input dot highlighted under the cursor during a wire drag

    // the last run's per-node results (node id -> preview payload)
    private Dictionary<string, JsonElement> _results = new();

    // the frontend graph's connections + the wire visuals (visual kept for M8c)
    private readonly List<Edge> _edges = new();
    private readonly List<Wire> _wires = new();
    private sealed record Wire(Path Path, Shape From, Shape To)
    {
        public Point FromOffset { get; set; }   // dot centers relative to their node top-left;
        public Point ToOffset { get; set; }      // mutable so a resize can refresh them
    }

    public MainWindow()
    {
        InitializeComponent();

        SyncTheme();                                     // pull theme colors into the shared brushes
        ActualThemeVariantChanged += (_, _) => SyncTheme();   // recolor on Dark/Light/System change
        CacheThemeItems();
        UpdateThemeChecks(Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default);

        // zoom + pan: transform the canvas content; the viewport Border clips it (#10)
        NodeCanvas.RenderTransform = new TransformGroup { Children = { _zoom, _pan } };
        NodeCanvas.RenderTransformOrigin = RelativePoint.TopLeft;   // scale about the canvas origin -> simple math
        InsertGrid();   // faint world-space grid behind the nodes -> zoom/pan is visible

        // canvas-level pointer handlers live on the VIEWPORT, not NodeCanvas: NodeCanvas only
        // hit-tests its own (untransformed) bounds, so panned-in regions would otherwise be dead.
        CanvasViewport.PointerWheelChanged += OnCanvasWheel;
        CanvasViewport.AddHandler(PointerPressedEvent, OnPanPressed, RoutingStrategies.Tunnel);  // right-drag pan, even over nodes
        CanvasViewport.PointerPressed += OnCanvasPressed;   // left-drag empty canvas -> marquee / deselect
        CanvasViewport.PointerMoved += OnCanvasMoved;
        CanvasViewport.PointerReleased += OnCanvasReleased;

        // drag a palette row onto the canvas -> drop a node where released (separate event
        // channel from the pointer handlers above, so it can't interfere with them)
        DragDrop.SetAllowDrop(CanvasViewport, true);
        CanvasViewport.AddHandler(DragDrop.DragOverEvent, OnCanvasDragOver);
        CanvasViewport.AddHandler(DragDrop.DropEvent, OnCanvasDrop);
        // window is a drop target too, so DragOver fires everywhere -> the ghost tracks the cursor
        // over the palette as well as the canvas (drops are still only accepted on the canvas).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);

        _backend = new Backend();     // launches the Python backend once
        ShowProtocolVersion();
        LoadNodes();
    }

    private void ShowProtocolVersion()
    {
        try
        {
            JsonElement response = _backend.Request("server_info");
            string? version = response
                .GetProperty("result")
                .GetProperty("protocol_version")
                .GetString();
            StatusText.Text = $"connected — protocol {version}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"backend error: {ex.Message}";
        }
    }

    private void LoadNodes()
    {
        LoadPalettePrefs();
        FetchManifest(status: true);
    }

    // Edit > Nodes > Show All Nodes -> re-filter the palette (no refetch; we hold every tier).
    // Pinned/Recent/quick-add always resolve from the full set regardless of this toggle.
    private void OnToggleShowAll(object? sender, EventArgs e)
    {
        _showAll = !_showAll;
        if (sender is NativeMenuItem mi) mi.IsChecked = _showAll;
        _collapsed.Clear();
        if (_showAll) foreach (string c in _allTypes.Select(t => t.Category).Distinct()) _collapsed.Add(c);  // start collapsed
        RebuildPalette(SearchBox.Text ?? "");
    }

    // pull the full manifest once (every tier); the palette filters by tier for display.
    private void FetchManifest(bool status)
    {
        try
        {
            JsonElement response = _backend.Request("get_manifest", new { tier = "all" });
            var types = new List<NodeType>();
            foreach (JsonElement node in response.GetProperty("result").EnumerateArray())
                types.Add(ParseNodeType(node));

            _allTypes = types;
            RebuildPalette(SearchBox.Text ?? "");
            if (status) StatusText.Text += $" · {types.Count(t => t.Tier == "common")} common / {types.Count} nodes";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"manifest error: {ex.Message}";
        }
    }

    // --- palette: search (#1) + category sections (#2) ---

    // categories the user has collapsed (remembered across rebuilds); empty = all expanded
    private readonly HashSet<string> _collapsed = new();

    private bool _showAll;                               // Edit > Nodes > Show All Nodes (tier=all vs common)
    private readonly List<string> _recent = new();      // recently added kinds, most-recent first
    private readonly HashSet<string> _pinned = new();   // pinned kinds (shown in a top section)
    private const int RecentMax = 10;
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
        Control? paramsPanel = BuildParams(node);
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

    private Control? BuildParams(NodeInstance node)
    {
        // skip params that are ALSO input ports (the "port-or-literal" ones) -> no double UI
        var portNames = node.Type.Inputs.Select(i => i.Name).ToHashSet();
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(10, 4, 10, 2) };
        foreach (ParamSpec p in node.Type.Params)
            if (!portNames.Contains(p.Name))
                panel.Children.Add(ParamRow(node, p));
        return panel.Children.Count == 0 ? null : panel;
    }

    private static Control ParamRow(NodeInstance node, ParamSpec p)
    {
        if (p.Widget == "kvlist") return BuildKvList(node, p);   // dynamic key/value row editor (dict.build)
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

    private static Control BuildKvList(NodeInstance node, ParamSpec p)
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
            del.Click += (_, _) => { rows.Remove(entry); rowsPanel.Children.Remove(row); Commit(); };
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
        add.Click += (_, _) => AddRow("", "", "auto");

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

    // --- port interaction: wiring ---

    private void OnPortPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;   // don't let this start a node drag
        if (sender is not Shape dot || dot.Tag is not PortRef port) return;
        if (port.IsInput) return;   // wires start from an OUTPUT port

        _wireSource = port;
        _wireSourceDot = dot;

        _wireStart = DotCenter(dot);
        _rubberBand = new Path
        {
            Stroke = TypeColor(port.Port.Type),   // wire takes its SOURCE port's type color
            StrokeThickness = 2.5,
            StrokeLineCap = PenLineCap.Round,
            Data = BezierGeometry(_wireStart, _wireStart),
        };
        NodeCanvas.Children.Add(_rubberBand);

        e.Pointer.Capture(dot);   // route the move/release events to this dot
    }

    private void OnPortMoved(object? sender, PointerEventArgs e)
    {
        if (_rubberBand is null) return;              // only while dragging a wire

        Point p = e.GetPosition(NodeCanvas);
        _rubberBand.Data = BezierGeometry(_wireStart, p);   // redraw the curve to the cursor

        // capture blocks the target dots' own hover events, so drive the highlight ourselves
        Shape? over = FindInputDotAt(p);
        if (!ReferenceEquals(over, _wireHoverDot))
        {
            if (_wireHoverDot is not null) SetPortHover(_wireHoverDot, false);   // restore the one we left
            if (over?.Tag is PortRef pr)
            {
                // green if this wire could legally drop here, red if not
                bool ok = CanConnect(_wireSource!, pr);
                SetPortEdge(over, ok ? Brushes.LimeGreen : Brushes.Red, 3);
            }
            _wireHoverDot = over;
        }
    }

    private void OnPortReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_wireSource is null) return;

        Shape? targetDot = FindInputDotAt(e.GetPosition(NodeCanvas));
        NodeCanvas.Children.Remove(_rubberBand!);         // discard the rubber-band; a real wire is made below
        if (targetDot?.Tag is PortRef target && CanConnect(_wireSource, target))
            AddWire(_wireSourceDot!, targetDot);

        if (_wireHoverDot is not null) { SetPortHover(_wireHoverDot, false); _wireHoverDot = null; }
        if (_wireSourceDot is not null) SetPortHover(_wireSourceDot, false);   // it was left hovered from the initial enter
        _wireSource = null;
        _wireSourceDot = null;
        _rubberBand = null;
        e.Pointer.Capture(null);
    }

    // create a wire between two dots (source out -> target in): record the edge, draw the curve
    // behind the nodes, make it selectable. Shared by interactive wiring and graph load.
    private void AddWire(Shape fromDot, Shape toDot)
    {
        var src = (PortRef)fromDot.Tag!;
        var tgt = (PortRef)toDot.Tag!;
        // non-variadic input: last wire wins -> drop any existing wire into the same port
        if (!tgt.Port.Variadic)
            foreach (Wire existing in _wires.Where(w => SamePort((PortRef)w.To.Tag!, tgt)).ToList())
                RemoveWire(existing);

        var path = new Path
        {
            Stroke = TypeColor(src.Port.Type),
            StrokeThickness = 2.5,
            StrokeLineCap = PenLineCap.Round,
            Data = BezierGeometry(DotCenter(fromDot), DotCenter(toDot)),
        };
        NodeCanvas.Children.Insert(0, path);   // index 0 = behind the node boxes
        path.PointerPressed += OnWirePressed;  // clickable -> selectable/deletable

        _edges.Add(new Edge(src.Node.Id, src.Port.Name, tgt.Node.Id, tgt.Port.Name));
        _wires.Add(new Wire(path, fromDot, toDot)
        {
            FromOffset = LocalOffset(fromDot),
            ToOffset = LocalOffset(toDot),
        });
    }

    // the canvas Border whose model is this node
    private Border? NodeBox(NodeInstance node) =>
        NodeCanvas.Children.OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.Tag, node));

    // the port dot for (node, port name, side) -- used to re-wire a loaded graph
    private Shape? FindDot(NodeInstance node, string portName, bool isInput)
    {
        Border? box = NodeBox(node);
        return box?.GetVisualDescendants().OfType<Shape>()
            .FirstOrDefault(s => s.Tag is PortRef pr && pr.Port.Name == portName && pr.IsInput == isInput);
    }

    // a Blender-style cubic bezier from a (source) to b (target): handles leave horizontally
    private static Geometry BezierGeometry(Point a, Point b)
    {
        double dx = Math.Max(40, Math.Abs(b.X - a.X) * 0.5);   // handle length
        var figure = new PathFigure
        {
            StartPoint = a,
            IsClosed = false,
            Segments = new PathSegments
            {
                new BezierSegment
                {
                    Point1 = new Point(a.X + dx, a.Y),   // handle out of the source (rightward)
                    Point2 = new Point(b.X - dx, b.Y),   // handle into the target (leftward)
                    Point3 = b,
                },
            },
        };
        return new PathGeometry { Figures = new PathFigures { figure } };
    }

    // a dot's center relative to its node's top-left (fixed; node layout never changes)
    private Point LocalOffset(Shape dot)
    {
        NodeInstance node = ((PortRef)dot.Tag!).Node;
        Point c = DotCenter(dot);
        return new Point(c.X - node.X, c.Y - node.Y);
    }

    // a dot's current canvas center = its node's live position + the fixed offset (no layout lag)
    private static Point Endpoint(Shape dot, Point offset)
    {
        NodeInstance node = ((PortRef)dot.Tag!).Node;
        return new Point(node.X + offset.X, node.Y + offset.Y);
    }

    // redraw every wire from its endpoints' live positions
    private void UpdateWires()
    {
        foreach (Wire w in _wires)
            w.Path.Data = BezierGeometry(Endpoint(w.From, w.FromOffset), Endpoint(w.To, w.ToOffset));
    }

    // center of a port dot, in canvas coordinates (the wire's endpoint)
    private Point DotCenter(Shape dot) =>
        dot.TranslatePoint(new Point(dot.Bounds.Width / 2, dot.Bounds.Height / 2), NodeCanvas) ?? default;

    // the input dot under a canvas point, if any
    private Shape? FindInputDotAt(Point p)
    {
        foreach (Visual v in NodeCanvas.GetVisualsAt(p))
            if (v is Shape dot && dot.Tag is PortRef pr && pr.IsInput)
                return dot;
        return null;
    }

    // same strict type rule the backend's connect() enforces
    private static bool CanConnect(PortRef source, PortRef target)
    {
        if (ReferenceEquals(source.Node, target.Node)) return false;   // no wiring a node to itself
        var strict = new HashSet<string> { "expr", "frame", "series" };
        string a = source.Port.Type, b = target.Port.Type;
        return !(strict.Contains(a) && strict.Contains(b) && a != b);
    }

    // set a port's EDGE (fill stays the type color)
    private static void SetPortEdge(Shape dot, IBrush stroke, double thickness)
    {
        dot.Stroke = stroke;
        dot.StrokeThickness = thickness;
    }

    // plain hover (white) / restore (thin dark)
    private static void SetPortHover(Shape dot, bool on) =>
        SetPortEdge(dot, on ? ThemeAccent : Brush("#1E1E1E"), on ? 3 : 1);

    private void OnPortEnter(object? sender, PointerEventArgs e)
    {
        if (_rubberBand is not null) return;          // during a wire drag, OnPortMoved drives the highlight
        if (sender is Shape dot) SetPortHover(dot, true);
    }

    private void OnPortExit(object? sender, PointerEventArgs e)
    {
        if (_rubberBand is not null) return;
        if (sender is Shape dot) SetPortHover(dot, false);
    }

    // --- selection + deletion ---

    private static NodeInstance DotNode(Shape dot) => ((PortRef)dot.Tag!).Node;
    private static bool SamePort(PortRef a, PortRef b) => ReferenceEquals(a.Node, b.Node) && a.Port.Name == b.Port.Name;
    private static IBrush WireColor(Wire w) => TypeColor(((PortRef)w.From.Tag!).Port.Type);

    // clear every selected node/wire back to its normal look
    private void ClearSelection()
    {
        foreach (Border b in _selNodes) b.BorderBrush = Brushes.SteelBlue;
        foreach (Wire w in _selWires) w.Path.Stroke = WireColor(w);
        foreach (Border bx in _selBoxes) bx.BorderThickness = new Thickness(1.5);
        _selNodes.Clear();
        _selWires.Clear();
        _selBoxes.Clear();
    }

    // select a node; additive (shift) toggles it into/out of the current set
    private void SelectNode(Border box, bool additive)
    {
        if (!additive) ClearSelection();
        if (additive && _selNodes.Remove(box)) box.BorderBrush = Brushes.SteelBlue;   // toggle off
        else { _selNodes.Add(box); box.BorderBrush = ThemeAccent; }
        SyncPanels();
    }

    private void SelectWire(Wire w, bool additive)
    {
        if (!additive) ClearSelection();
        if (additive && _selWires.Remove(w)) w.Path.Stroke = WireColor(w);
        else { _selWires.Add(w); w.Path.Stroke = ThemeAccent; }
        SyncPanels();
    }

    private void DeselectAll() { ClearSelection(); SyncPanels(); }

    // the single selected node, or null when zero/many are selected
    private NodeInstance? SoleNode() =>
        _selNodes.Count == 1 && _selNodes.First().Tag is NodeInstance ni ? ni : null;

    // info + output panels track the sole selected node; a hint otherwise
    private void SyncPanels()
    {
        NodeInstance? only = SoleNode();
        InfoArea.Content = only is not null
            ? BuildInfo(only.Type)
            : new TextBlock
            {
                Text = _selNodes.Count > 1 ? $"{_selNodes.Count} nodes selected" : "(click a node)",
                Foreground = ThemeSubtext,
            };
        RefreshOutput();
    }

    private void OnWirePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Path path) return;
        Wire? w = _wires.FirstOrDefault(x => ReferenceEquals(x.Path, path));
        if (w is not null) { SelectWire(w, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
    }

    // --- canvas pointer: marquee (left) + group box (right) + pan (middle) + zoom (wheel) ---

    // a faint tiling grid drawn in world space (inside NodeCanvas) -> it pans/zooms with the content,
    // so zoom level is visible. Non-hit-testable so it never intercepts pointer/marquee.
    private void InsertGrid()
    {
        const double cell = 25, span = 40000;
        var tile = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            SourceRect = new RelativeRect(0, 0, cell, cell, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, cell, cell, RelativeUnit.Absolute),
            Drawing = new GeometryDrawing
            {
                Geometry = new RectangleGeometry(new Rect(0, 0, cell, cell)),
                Pen = new Pen(ThemeGrid, 1),
            },
        };
        var grid = new Rectangle { Width = span, Height = span, Fill = tile, IsHitTestVisible = false, ZIndex = -2 };
        Canvas.SetLeft(grid, -span / 2);
        Canvas.SetTop(grid, -span / 2);
        NodeCanvas.Children.Insert(0, grid);   // ZIndex -2 keeps it behind group boxes (-1), wires+nodes (0)
    }

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        double newScale = Math.Clamp(_zoom.ScaleX * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), 0.2, 3.0);
        Point w = e.GetPosition(NodeCanvas);        // world point under the cursor (via current transform)
        Point sc = e.GetPosition(CanvasViewport);   // same point in viewport/screen space
        _zoom.ScaleX = _zoom.ScaleY = newScale;
        _pan.X = sc.X - newScale * w.X;             // keep w pinned under the cursor
        _pan.Y = sc.Y - newScale * w.Y;
        UpdateWires();
        e.Handled = true;
    }

    private void OnPanPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(CanvasViewport).Properties;
        if (props.IsMiddleButtonPressed)                    // middle-drag pans
        {
            _panning = true;
            _panStart = e.GetPosition(CanvasViewport);
            _panOrigin = new Point(_pan.X, _pan.Y);
            e.Pointer.Capture(CanvasViewport);
            e.Handled = true;   // stop the press reaching a node (no drag)
            return;
        }
        if (props.IsRightButtonPressed)                     // right-drag draws a group box
        {
            Color c = _boxColors[_boxColorIdx % _boxColors.Length];
            _boxStart = e.GetPosition(NodeCanvas);
            _drawingBox = new Border
            {
                Background = BoxFill(c),
                BorderBrush = new SolidColorBrush(c),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                ZIndex = -1,   // above the grid (-2), behind wires+nodes (0)
            };
            Canvas.SetLeft(_drawingBox, _boxStart.X);
            Canvas.SetTop(_drawingBox, _boxStart.Y);
            NodeCanvas.Children.Add(_drawingBox);
            e.Pointer.Capture(CanvasViewport);
            e.Handled = true;   // stop the press reaching a node / the marquee
        }
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        // only an empty-canvas LEFT press starts a marquee / deselect
        if (!ReferenceEquals(e.Source, NodeCanvas) && !ReferenceEquals(e.Source, CanvasViewport)) return;
        if (!e.GetCurrentPoint(NodeCanvas).Properties.IsLeftButtonPressed) return;

        NodeCanvas.Focus();   // pull focus off any param TextBox -> it commits
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) DeselectAll();

        _marqueeing = true;
        _marqueeStart = e.GetPosition(NodeCanvas);
        _marquee = new Rectangle
        {
            Stroke = ThemeAccent,
            StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        };
        Canvas.SetLeft(_marquee, _marqueeStart.X);
        Canvas.SetTop(_marquee, _marqueeStart.Y);
        NodeCanvas.Children.Add(_marquee);
        e.Pointer.Capture(CanvasViewport);
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        _lastCanvasWorld = e.GetPosition(NodeCanvas);   // remembered for Shift+A placement
        if (_panning)
        {
            Point s = e.GetPosition(CanvasViewport);
            _pan.X = _panOrigin.X + (s.X - _panStart.X);
            _pan.Y = _panOrigin.Y + (s.Y - _panStart.Y);
            UpdateWires();
            return;
        }
        if (_drawingBox is not null)   // rubber-banding a new group box
        {
            Point q = e.GetPosition(NodeCanvas);
            Canvas.SetLeft(_drawingBox, Math.Min(q.X, _boxStart.X));
            Canvas.SetTop(_drawingBox, Math.Min(q.Y, _boxStart.Y));
            _drawingBox.Width = Math.Abs(q.X - _boxStart.X);
            _drawingBox.Height = Math.Abs(q.Y - _boxStart.Y);
            return;
        }
        if (_draggingBox is not null)  // moving an existing group box
        {
            Point q = e.GetPosition(NodeCanvas);
            Canvas.SetLeft(_draggingBox, _boxDragOrigin.X + (q.X - _boxDragStart.X));
            Canvas.SetTop(_draggingBox, _boxDragOrigin.Y + (q.Y - _boxDragStart.Y));
            return;
        }
        if (!_marqueeing || _marquee is null) return;
        Point p = e.GetPosition(NodeCanvas);
        Canvas.SetLeft(_marquee, Math.Min(p.X, _marqueeStart.X));
        Canvas.SetTop(_marquee, Math.Min(p.Y, _marqueeStart.Y));
        _marquee.Width = Math.Abs(p.X - _marqueeStart.X);
        _marquee.Height = Math.Abs(p.Y - _marqueeStart.Y);
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning) { _panning = false; e.Pointer.Capture(null); return; }
        if (_drawingBox is not null)   // finish drawing a group box
        {
            e.Pointer.Capture(null);
            if (_drawingBox.Width < 20 || _drawingBox.Height < 20)   // ignore an accidental click/tiny drag
                NodeCanvas.Children.Remove(_drawingBox);
            else { FinalizeBox(_drawingBox, _boxColorIdx); _boxColorIdx++; }
            _drawingBox = null;
            return;
        }
        if (_draggingBox is not null) { _draggingBox = null; e.Pointer.Capture(null); return; }
        if (!_marqueeing) return;
        _marqueeing = false;
        if (_marquee is not null)
        {
            var r = new Rect(Canvas.GetLeft(_marquee), Canvas.GetTop(_marquee), _marquee.Width, _marquee.Height);
            NodeCanvas.Children.Remove(_marquee);
            _marquee = null;
            foreach (Border b in NodeCanvas.Children.OfType<Border>())
                if (b.Tag is NodeInstance n && r.Intersects(new Rect(n.X, n.Y, b.Bounds.Width, b.Bounds.Height))
                    && _selNodes.Add(b))
                    b.BorderBrush = ThemeAccent;
            SyncPanels();
        }
        e.Pointer.Capture(null);
    }

    // turn the just-drawn rectangle into a real group box: header (color swatch + name), drag/select handlers
    private void FinalizeBox(Border box, int colorIdx)
    {
        var gb = new GroupBox { ColorIdx = colorIdx };
        box.Tag = gb;
        Color c = _boxColors[colorIdx % _boxColors.Length];

        gb.Swatch = new Button
        {
            Width = 14, Height = 14, Padding = new Thickness(0),
            Background = new SolidColorBrush(c), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        gb.Swatch.Click += (_, _) => SetBoxColor(box, (gb.ColorIdx + 1) % _boxColors.Length);

        gb.Name = new TextBox
        {
            PlaceholderText = "group", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = ThemeText, FontWeight = FontWeight.Bold, FontSize = 12,
            Padding = new Thickness(2, 0), MinWidth = 60,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
        };
        header.Children.Add(gb.Swatch);
        header.Children.Add(gb.Name);
        box.Child = header;               // the rest of the border stays empty -> drag surface
        box.PointerPressed += OnBoxPressed;
    }

    // rebuild a group box from saved geometry (load path)
    private Border AddBox(double x, double y, double w, double h, string name, int colorIdx)
    {
        Color c = _boxColors[colorIdx % _boxColors.Length];
        var box = new Border
        {
            Background = BoxFill(c), BorderBrush = new SolidColorBrush(c),
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
            ZIndex = -1, Width = w, Height = h,
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        NodeCanvas.Children.Add(box);
        FinalizeBox(box, colorIdx);
        ((GroupBox)box.Tag!).Name.Text = name;
        return box;
    }

    private void SetBoxColor(Border box, int idx)
    {
        if (box.Tag is not GroupBox gb) return;
        gb.ColorIdx = idx;
        Color c = _boxColors[idx % _boxColors.Length];
        box.Background = BoxFill(c);
        box.BorderBrush = new SolidColorBrush(c);
        gb.Swatch.Background = new SolidColorBrush(c);
    }

    private void SelectBox(Border box, bool additive)
    {
        if (!additive) ClearSelection();
        if (additive && _selBoxes.Remove(box)) box.BorderThickness = new Thickness(1.5);
        else { _selBoxes.Add(box); box.BorderThickness = new Thickness(3); }
        SyncPanels();
    }

    private void OnBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border box) return;
        if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
        if (e.Source is TextBox or Button) return;   // editing the name / cycling color, not a drag
        SelectBox(box, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _draggingBox = box;
        _boxDragStart = e.GetPosition(NodeCanvas);
        _boxDragOrigin = new Point(Canvas.GetLeft(box), Canvas.GetTop(box));
        e.Pointer.Capture(CanvasViewport);
        e.Handled = true;   // don't also deselect / start a marquee
    }

    private void RemoveWire(Wire w)
    {
        NodeCanvas.Children.Remove(w.Path);
        _wires.Remove(w);
        var src = (PortRef)w.From.Tag!;
        var tgt = (PortRef)w.To.Tag!;
        _edges.RemoveAll(edge => edge.SourceId == src.Node.Id && edge.SourceOut == src.Port.Name
                              && edge.TargetId == tgt.Node.Id && edge.TargetIn == tgt.Port.Name);
        _selWires.Remove(w);
    }

    private void RemoveNode(Border box)
    {
        if (box.Tag is not NodeInstance node) return;
        // cascade: delete every wire touching this node (as source or target)
        foreach (Wire w in _wires.Where(w => DotNode(w.From) == node || DotNode(w.To) == node).ToList())
            RemoveWire(w);
        NodeCanvas.Children.Remove(box);
        _nodes.Remove(node);
        _selNodes.Remove(box);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Shift+A -> quick-add search at the cursor (Blender-style), unless typing in a text field
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
        {
            OpenQuickAdd();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Delete or Key.Back)
        {
            foreach (Wire w in _selWires.ToList()) RemoveWire(w);
            foreach (Border b in _selNodes.ToList()) RemoveNode(b);
            foreach (Border bx in _selBoxes.ToList()) { NodeCanvas.Children.Remove(bx); _selBoxes.Remove(bx); }
            SyncPanels();
        }
        base.OnKeyDown(e);
    }

    // one row in the quick-add list: a category to drill into, or a node to add
    private sealed record QaItem(bool IsCategory, string Category, NodeType? Node);

    // Shift+A: Blender-style add menu at the cursor. Categories first (drill in with →/Enter,
    // back with ←/Backspace); type anywhere to search across every node.
    private void OpenQuickAdd()
    {
        if (_quickAdd is not null) return;   // already open
        _qaCategory = null;

        var search = new TextBox { PlaceholderText = "Search nodes…", MinWidth = 250, Margin = new Thickness(0, 0, 0, 4) };
        var crumb = new TextBlock
        {
            Foreground = ThemeSubtext, FontSize = 11, Margin = new Thickness(2, 0, 0, 4),
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
        };
        var list = new ListBox
        {
            MaxHeight = 320, MinWidth = 250, Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<QaItem>((it, _) => QaRow(it), true),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())   // slim rows: drop the default container padding/height
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(2, 0)),
                new Setter(Layoutable.MinHeightProperty, 0.0),
            },
        });

        void Refresh()
        {
            string q = search.Text?.Trim() ?? "";
            List<QaItem> items;
            if (q.Length > 0)                                  // search: flat, across everything
                items = _allTypes.Where(t => t.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || t.Kind.Contains(q, StringComparison.OrdinalIgnoreCase))
                                 .Take(60).Select(t => new QaItem(false, t.Category, t)).ToList();
            else if (_qaCategory is null)                      // top level: category list
                items = _allTypes.Select(t => t.Category).Distinct()
                                 .Select(c => new QaItem(true, c, null)).ToList();
            else                                               // inside a category: its nodes
                items = _allTypes.Where(t => t.Category == _qaCategory)
                                 .Select(t => new QaItem(false, t.Category, t)).ToList();
            list.ItemsSource = items;
            if (items.Count > 0) list.SelectedIndex = 0;
            crumb.IsVisible = _qaCategory is not null && q.Length == 0;
            crumb.Text = $"‹  {_qaCategory}";
        }

        void Commit(NodeType? t)
        {
            if (t is not null) { AddNode(t, _lastCanvasWorld.X, _lastCanvasWorld.Y, $"n{_nextId++}•"); PushRecent(t.Kind); }
            CloseQuickAdd();
        }
        void Activate(QaItem? it)
        {
            if (it is null) return;
            if (it.IsCategory) { _qaCategory = it.Category; search.Text = ""; Refresh(); }   // drill in
            else Commit(it.Node);
        }
        void Back() { if (_qaCategory is not null) { _qaCategory = null; search.Text = ""; Refresh(); } }

        Refresh();
        search.TextChanged += (_, _) => Refresh();
        crumb.PointerPressed += (_, _) => Back();
        list.Tapped += (_, _) => Activate(list.SelectedItem as QaItem);   // single click navigates/adds
        search.KeyDown += (_, ke) =>
        {
            switch (ke.Key)
            {
                case Key.Enter or Key.Right: Activate(list.SelectedItem as QaItem); ke.Handled = true; break;
                case Key.Escape: CloseQuickAdd(); ke.Handled = true; break;
                case Key.Left: if (string.IsNullOrEmpty(search.Text)) { Back(); ke.Handled = true; } break;
                case Key.Back: if (string.IsNullOrEmpty(search.Text) && _qaCategory is not null) { Back(); ke.Handled = true; } break;
                case Key.Down when list.ItemCount > 0: list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.ItemCount - 1); ke.Handled = true; break;
                case Key.Up when list.ItemCount > 0: list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0); ke.Handled = true; break;
            }
        };

        _quickAdd = new Popup
        {
            PlacementTarget = NodeCanvas, Placement = PlacementMode.Pointer, IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = ThemeNodeBg, BorderBrush = ThemeAccent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8), MinWidth = 250,
                Child = new StackPanel { Children = { search, crumb, list } },
            },
        };
        _quickAdd.Closed += (_, _) => CloseQuickAdd();
        _quickAdd.Opened += (_, _) => search.Focus();
        NodeCanvas.Children.Add(_quickAdd);
        _quickAdd.Open();
    }

    // a quick-add row: category (swatch + name + ▸) or node (mini icon + label)
    private static Control QaRow(QaItem? it)
    {
        if (it is null) return new Control();
        if (it.IsCategory)
        {
            var dp = new DockPanel { LastChildFill = true };
            var chev = new TextBlock { Text = "▸", Foreground = ThemeSubtext, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(chev, Dock.Right);
            dp.Children.Add(chev);
            dp.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(2),
                                         Background = CategoryBrush(it.Category), Margin = new Thickness(0, 0, 7, 0),
                                         VerticalAlignment = VerticalAlignment.Center });
            dp.Children.Add(new TextBlock { Text = it.Category, Foreground = ThemeText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            return new Border { Padding = new Thickness(4, 1), Child = dp };
        }
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(BuildNodeIcon(it.Node!, 14));
        sp.Children.Add(new TextBlock { Text = it.Node!.Label, Foreground = ThemeText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Padding = new Thickness(4, 1), Child = sp };
    }

    private void CloseQuickAdd()
    {
        if (_quickAdd is null) return;
        Popup p = _quickAdd;
        _quickAdd = null;
        _qaCategory = null;
        p.IsOpen = false;
        NodeCanvas.Children.Remove(p);
    }

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

            JsonElement resp = _backend.Request("run_graph", new { graph = graphDoc });
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

    // --- Edit > Group into Node: turn the selected nodes into one composite (macro) node ---
    // Auto-detects the boundary from the wires crossing the selection: edges coming IN from
    // outside -> composite inputs (io.input); the single result leaving the selection (or the
    // lone terminal node) -> the output (io.output). v1: one output, current param values baked in.
    private async void OnGroupNodes(object? sender, EventArgs e)
    {
        List<NodeInstance> sel = _selNodes.Select(b => b.Tag as NodeInstance).OfType<NodeInstance>().ToList();
        if (sel.Count == 0) { StatusText.Text = "group: select one or more nodes first"; return; }
        var S = new HashSet<string>(sel.Select(n => n.Id));

        // distinct (source, out) leaving the selection
        var outward = _edges.Where(x => S.Contains(x.SourceId) && !S.Contains(x.TargetId))
                            .Select(x => (x.SourceId, x.SourceOut)).Distinct().ToList();

        (string id, string outp)? output = null;
        if (outward.Count == 1) output = outward[0];
        else if (outward.Count == 0)
        {
            List<NodeInstance> terminals = sel.Where(n => n.Type.Outputs.Count > 0 && !_edges.Any(x => x.SourceId == n.Id)).ToList();
            if (terminals.Count == 1) output = (terminals[0].Id, terminals[0].Type.Outputs[0].Name);
        }
        if (output is null)
        {
            StatusText.Text = outward.Count > 1
                ? "group: more than one output leaves the selection (v1 supports one)"
                : "group: no single output found — select a pipeline with exactly one result";
            return;
        }

        // one input per selected node input-port not fed from inside the selection:
        //  - a port with external wire(s) -> one input per external wire
        //  - an unconnected REQUIRED port  -> one input (e.g. filter.self with no upstream -> the frame input)
        //  - internally-fed / optional-unconnected ports -> nothing (baked, or left to the literal fallback)
        var drafts = new List<InputDraft>();
        int k = 0;
        foreach (NodeInstance n in sel)
            foreach (Port p in n.Type.Inputs)
            {
                List<Edge> srcs = _edges.Where(x => x.TargetId == n.Id && x.TargetIn == p.Name).ToList();
                List<Edge> ext = srcs.Where(x => !S.Contains(x.SourceId)).ToList();
                bool internallyFed = srcs.Any(x => S.Contains(x.SourceId));
                string dflt = p.Name == "self" ? (p.Type == "frame" ? "df" : p.Type) : p.Name;
                if (ext.Count > 0)
                    foreach (Edge _ in ext) drafts.Add(new InputDraft($"__in{k++}", n.Id, p.Name, p.Type, dflt));
                else if (!internallyFed && !p.Optional)
                    drafts.Add(new InputDraft($"__in{k++}", n.Id, p.Name, p.Type, dflt));
            }

        // every inner param is promotable -> the dialog ticks which become composite params (rest baked)
        var pdrafts = new List<ParamDraft>();
        foreach (NodeInstance n in sel)
            foreach (ParamSpec ps in n.Type.Params)
                pdrafts.Add(new ParamDraft(n.Id, n.Type.Label, ps.Name, ps.Type,
                                           n.Params.TryGetValue(ps.Name, out object? v) ? v : ps.Default));

        NodeInstance outNode = sel.First(n => n.Id == output.Value.id);
        string outType = outNode.Type.Outputs.FirstOrDefault(p => p.Name == output.Value.outp)?.Type ?? "any";

        // the form: name + description + input names/docs + params to expose + output name/doc (all feed the Info panel)
        CompositeForm? form = await PromptComposite(drafts, pdrafts, outType);
        if (form is null) return;

        // build the inner sub-graph doc: selected nodes (params baked) + io.input/io.output boundaries
        var nodeInputs = sel.ToDictionary(n => n.Id, _ => new Dictionary<string, List<string[]>>());
        void AddRef(string tgt, string port, string srcId, string srcOut)
        {
            if (!nodeInputs[tgt].TryGetValue(port, out List<string[]>? l)) nodeInputs[tgt][port] = l = new List<string[]>();
            l.Add(new[] { srcId, srcOut });
        }
        foreach (Edge x in _edges.Where(x => S.Contains(x.TargetId) && S.Contains(x.SourceId)))
            AddRef(x.TargetId, x.TargetIn, x.SourceId, x.SourceOut);   // internal wires kept as-is

        var subNodes = new List<object>();
        var inputOf = new Dictionary<string, string>();
        var compInputs = new List<object>();
        foreach ((InputDraft d, string cnameRaw, string cdoc) in form.Inputs)
        {
            subNodes.Add(new { id = d.InId, kind = "io.input", @params = new Dictionary<string, object?>(),
                               inputs = new Dictionary<string, List<string[]>>() });
            string cname = UniqueKey(inputOf.Keys, cnameRaw);
            inputOf[cname] = d.InId;
            compInputs.Add(new { name = cname, type = d.Type, doc = cdoc });
            AddRef(d.TargetId, d.TargetIn, d.InId, "out");
        }
        foreach (NodeInstance n in sel)
            subNodes.Add(new { id = n.Id, kind = n.Type.Kind, @params = n.Params, inputs = nodeInputs[n.Id] });

        subNodes.Add(new { id = "__out", kind = "io.output", @params = new Dictionary<string, object?>(),
                           inputs = new Dictionary<string, List<string[]>> { ["value"] = new() { new[] { output.Value.id, output.Value.outp } } } });

        // exposed (promoted) inner params -> composite params + a promotion map the backend applies at run
        var compParams = new List<object>();
        var promoted = new List<object>();
        var pNames = new HashSet<string>();
        foreach ((ParamDraft d, string pnameRaw, object? pdefault, string pdoc) in form.Promoted)
        {
            string pname = UniqueKey(pNames, pnameRaw);
            pNames.Add(pname);
            promoted.Add(new object?[] { d.NodeId, d.Param, pname, pdefault });
            compParams.Add(new { name = pname, type = d.Type, @default = pdefault, required = false, doc = pdoc });
        }

        var definition = new
        {
            name = form.Name,
            doc = form.Doc,
            inputs = compInputs,
            @params = compParams,
            outputs = new[] { new { name = form.OutputName, type = outType, doc = form.OutputDoc } },
            input_of = inputOf,
            promoted,
            output_node = "__out",
            subgraph = new { nodes = subNodes },
        };

        try
        {
            JsonElement resp = _backend.Request("create_node", new { definition });
            if (resp.TryGetProperty("error", out JsonElement err))
            {
                StatusText.Text = $"group error: {err.GetProperty("message").GetString()}";
                return;
            }
            NodeType t = ParseNodeType(resp.GetProperty("result"));
            _allTypes.RemoveAll(x => x.Kind == t.Kind);   // re-group replaces
            _allTypes.Add(t);
            RebuildPalette(SearchBox.Text ?? "");
            StatusText.Text = $"created {t.Label} · palette › User generated";
        }
        catch (Exception ex) { StatusText.Text = $"group error: {ex.Message}"; }
    }

    // a key not already in `taken` (append _2, _3, … on collision)
    private static string UniqueKey(IEnumerable<string> taken, string want)
    {
        var set = new HashSet<string>(taken);
        if (!set.Contains(want)) return want;
        for (int i = 2; ; i++) if (!set.Contains($"{want}_{i}")) return $"{want}_{i}";
    }

    // one auto-detected composite input, before the user names it
    private sealed record InputDraft(string InId, string TargetId, string TargetIn, string Type, string DefaultName);

    // one promotable inner param (a widget on a selected node that could become a composite param)
    private sealed record ParamDraft(string NodeId, string NodeLabel, string Param, string Type, object? Current);

    // the filled-in Create-Node form
    private sealed class CompositeForm
    {
        public string Name = "";
        public string Doc = "";
        public List<(InputDraft Draft, string Name, string Doc)> Inputs = new();
        public List<(ParamDraft Draft, string Name, object? Default, string Doc)> Promoted = new();
        public string OutputName = "out";
        public string OutputDoc = "";
    }

    // best-effort typed parse of a param default the user typed into the dialog
    private static object? ParseByType(string? text, string type) => (text ?? "").Trim() switch
    {
        "" => null,
        var s when type == "int" && long.TryParse(s, out long l) => l,
        var s when type == "float" && double.TryParse(s, out double d) => d,
        var s when type == "bool" && bool.TryParse(s, out bool b) => b,
        var s => s,
    };

    // the Create-Node dialog: name + description + each input's name/description + the output's name/description.
    // Everything here lands on the node's manifest entry, so the main-editor Info panel shows it like any node.
    private async Task<CompositeForm?> PromptComposite(List<InputDraft> drafts, List<ParamDraft> pdrafts, string outType)
    {
        var nameBox = new TextBox { Text = "My node", PlaceholderText = "node name", MinWidth = 300 };
        var descBox = new TextBox
        {
            PlaceholderText = "what this node does (shown in the Info panel)",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MinWidth = 300,
        };

        var inputRows = new List<(InputDraft Draft, TextBox Name, TextBox Doc)>();
        var inputsPanel = new StackPanel { Spacing = 6 };
        foreach (InputDraft d in drafts)
        {
            var nb = new TextBox { Text = d.DefaultName, MinWidth = 110 };
            var db = new TextBox { PlaceholderText = "description (optional)", MinWidth = 190 };
            inputRows.Add((d, nb, db));
            inputsPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { Chip(d.Type, TypeColor(d.Type)), nb, db },
            });
        }

        var outNameBox = new TextBox { Text = "out", MinWidth = 110 };
        var outDocBox = new TextBox { PlaceholderText = "description (optional)", MinWidth = 190 };

        var body = new StackPanel { Margin = new Thickness(16), Spacing = 8, Children =
        {
            new TextBlock { Text = "Create Node", FontWeight = FontWeight.Bold, FontSize = 15 },
            new TextBlock { Text = "Name", FontWeight = FontWeight.SemiBold, FontSize = 12 }, nameBox,
            new TextBlock { Text = "Description", FontWeight = FontWeight.SemiBold, FontSize = 12 }, descBox,
        }};
        if (drafts.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Inputs", FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            body.Children.Add(inputsPanel);
        }

        // Params: tick an inner param to expose it as a composite param (editable field on the node);
        // unticked params keep their current value baked in.
        var paramRows = new List<(ParamDraft Draft, CheckBox On, TextBox Name, TextBox Default, TextBox Doc)>();
        if (pdrafts.Count > 0)
        {
            var paramsPanel = new StackPanel { Spacing = 6 };
            foreach (ParamDraft pd in pdrafts)
            {
                var on = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                var nb = new TextBox { Text = pd.Param, MinWidth = 100 };
                var vb = new TextBox { Text = pd.Current?.ToString() ?? "", MinWidth = 90 };
                var db = new TextBox { PlaceholderText = "description (optional)", MinWidth = 150 };
                paramRows.Add((pd, on, nb, vb, db));
                paramsPanel.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        on,
                        new TextBlock { Text = $"{pd.NodeLabel} · {pd.Param}", FontSize = 11, Foreground = ThemeSubtext,
                                        VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 },
                        nb, Chip(pd.Type, Brush("#4A5A7A")), vb, db,
                    },
                });
            }
            body.Children.Add(new TextBlock { Text = "Params  (tick to expose; unticked = baked at current value)",
                                              FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            body.Children.Add(paramsPanel);
        }

        body.Children.Add(new TextBlock { Text = "Output", FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { Chip(outType, TypeColor(outType)), outNameBox, outDocBox },
        });

        var ok = new Button { Content = "Create", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0), Children = { cancel, ok },
        });

        CompositeForm? result = null;
        var dlg = new Window
        {
            Title = "Create Node", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, MaxWidth = 640,
            Content = new ScrollViewer { MaxHeight = 460, Content = body },
        };
        ok.Click += (_, _) =>
        {
            var f = new CompositeForm
            {
                Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "My node" : nameBox.Text.Trim(),
                Doc = descBox.Text?.Trim() ?? "",
                OutputName = string.IsNullOrWhiteSpace(outNameBox.Text) ? "out" : outNameBox.Text.Trim(),
                OutputDoc = outDocBox.Text?.Trim() ?? "",
            };
            foreach ((InputDraft d, TextBox nb, TextBox db) in inputRows)
                f.Inputs.Add((d, string.IsNullOrWhiteSpace(nb.Text) ? d.DefaultName : nb.Text.Trim(), db.Text?.Trim() ?? ""));
            foreach ((ParamDraft d, CheckBox on, TextBox nb, TextBox vb, TextBox db) in paramRows)
                if (on.IsChecked == true)
                    f.Promoted.Add((d, string.IsNullOrWhiteSpace(nb.Text) ? d.Param : nb.Text.Trim(),
                                    ParseByType(vb.Text, d.Type), db.Text?.Trim() ?? ""));
            result = f;
            dlg.Close();
        };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

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
            JsonElement resp = _backend.Request("import_node", new { path = files[0].Path.LocalPath });
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
    protected override void OnClosed(EventArgs e)
    {
        _backend.Dispose();
        base.OnClosed(e);
    }
}
