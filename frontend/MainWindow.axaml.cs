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
    // one Backend for the app's lifetime -> Python starts once and stays running (null if start failed)
    private readonly Backend? _backend;

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
    private sealed class TextNote { public TextBox Text = null!; }
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

        try
        {
            _backend = new Backend();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"backend failed: {ex.Message}";
            RunButton.IsEnabled = false;
            return;
        }
        ShowProtocolVersion();
        LoadNodes();
    }

    private JsonElement? BackendRequest(string method, object? parameters = null)
    {
        if (_backend is null) { StatusText.Text = "backend not connected"; return null; }
        return _backend.Request(method, parameters);
    }

    private void ShowProtocolVersion()
    {
        try
        {
            JsonElement response = BackendRequest("server_info") ?? default;
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
            JsonElement? r = BackendRequest("get_manifest", new { tier = "all" });
            if (r is null) return;
            JsonElement response = r.Value;
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
        // Ctrl+Alt+. -> center viewport on the center-of-mass of all nodes
        if (e.Key == Key.OemPeriod
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            CenterOnNodes();
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

    // runs when the window closes -> shut the Python backend down (no stray processes)
    protected override void OnClosed(EventArgs e)
    {
        _backend?.Dispose();
        base.OnClosed(e);
    }
}
