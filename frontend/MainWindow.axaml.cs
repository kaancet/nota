using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Nota;

public partial class MainWindow : Window
{
    // one Backend for the app's lifetime -> Python starts once and stays running
    private readonly Backend _backend;

    // the frontend's copy of the graph: every node dropped on the canvas
    private readonly List<NodeInstance> _nodes = new();
    private int _nextId;      // used to hand out unique ids: n0, n1, n2, ...

    private Border? _dragging;   // the node currently being dragged (null = none)
    private Point _grabOffset;   // where inside the node the pointer grabbed it
    private bool _nodeMoved;     // did the current press actually drag, or was it a click?

    // current selection (at most one of these is set) -> Delete key removes it
    private Border? _selectedNode;
    private Wire? _selectedWire;

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
    private sealed record Wire(Path Path, Shape From, Shape To, Point FromOffset, Point ToOffset);

    public MainWindow()
    {
        InitializeComponent();
        NodeCanvas.PointerPressed += OnCanvasPressed;   // click empty canvas -> deselect
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
        try
        {
            JsonElement response = _backend.Request("get_manifest");

            // parse each manifest entry into a NodeType object (keeps the whole schema)
            var types = new List<NodeType>();
            foreach (JsonElement node in response.GetProperty("result").EnumerateArray())
                types.Add(ParseNodeType(node));

            NodeList.ItemsSource = types;                  // items are NodeType objects now
            StatusText.Text += $" · {types.Count} nodes";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"manifest error: {ex.Message}";
        }
    }

    // turn one manifest JSON entry into a NodeType
    private static NodeType ParseNodeType(JsonElement node) => new NodeType(
        Kind: node.GetProperty("kind").GetString()!,
        Label: node.GetProperty("label").GetString()!,
        Category: node.GetProperty("category").GetString()!,
        Tier: node.GetProperty("tier").GetString()!,
        Inputs: ParsePorts(node.GetProperty("inputs")),
        Outputs: ParsePorts(node.GetProperty("outputs")));

    // turn an "inputs"/"outputs" JSON array into a list of Port objects
    private static List<Port> ParsePorts(JsonElement array)
    {
        var ports = new List<Port>();
        foreach (JsonElement p in array.EnumerateArray())
            ports.Add(new Port(
                p.GetProperty("name").GetString()!,
                p.GetProperty("type").GetString()!,
                p.GetProperty("variadic").GetBoolean(),
                p.GetProperty("optional").GetBoolean()));
        return ports;
    }

    // double-click a palette item -> drop a node box onto the canvas
    private void OnNodeActivated(object? sender, TappedEventArgs e)
    {
        if (NodeList.SelectedItem is not NodeType type)
            return;   // nothing selected

        // 1) give the node an identity in our graph
        double offset = 40 + (_nodes.Count % 12) * 26;   // cascade so drops don't stack
        var instance = new NodeInstance($"n{_nextId++}•", type, offset, offset);
        _nodes.Add(instance);

        // 2) build its visual (header + ports), linked to its model via Tag
        var box = BuildNodeVisual(instance);

        // make it draggable: subscribe to the pointer events
        box.PointerPressed += OnNodePressed;
        box.PointerMoved += OnNodeMoved;
        box.PointerReleased += OnNodeReleased;

        // 3) place it on the canvas at the instance's position
        Canvas.SetLeft(box, instance.X);
        Canvas.SetTop(box, instance.Y);
        NodeCanvas.Children.Add(box);
    }

    // --- dragging a node around the canvas ---

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border box) return;
        _dragging = box;
        _nodeMoved = false;                 // reset; a press with no move is a click (select)
        _grabOffset = e.GetPosition(box);   // pointer position WITHIN the node
        e.Pointer.Capture(box);             // keep receiving move/release even if pointer leaves the box
    }

    private void OnNodeMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null) return;
        _nodeMoved = true;                             // it's a drag, not a click

        Point p = e.GetPosition(NodeCanvas);           // pointer position on the canvas
        double x = p.X - _grabOffset.X;                // keep the grabbed point under the cursor
        double y = p.Y - _grabOffset.Y;

        // keep the node inside the canvas (leave a 6px margin so the poking dots stay visible)
        const double m = 6;
        x = Math.Clamp(x, m, Math.Max(m, NodeCanvas.Bounds.Width - _dragging.Bounds.Width - m));
        y = Math.Clamp(y, m, Math.Max(m, NodeCanvas.Bounds.Height - _dragging.Bounds.Height - m));

        Canvas.SetLeft(_dragging, x);                  // move the visual
        Canvas.SetTop(_dragging, y);
        if (_dragging.Tag is NodeInstance node)        // and update the model's position
        {
            node.X = x;
            node.Y = y;
        }

        UpdateWires();   // redraw any wires attached to the moved node
    }

    private void OnNodeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_nodeMoved && _dragging is not null)
            Select(_dragging, null);        // a click (no drag) selects the node
        _dragging = null;
        e.Pointer.Capture(null);            // release the pointer
    }

    // --- building a node's visual: header on top, input ports left, output ports right ---

    private Border BuildNodeVisual(NodeInstance node)
    {
        // colored title bar -- the foundation for category color-coding
        var titleBar = new Border
        {
            Background = CategoryBrush(node.Type.Category),
            CornerRadius = new CornerRadius(6, 6, 0, 0),   // round only the top, matching the box
            Padding = new Thickness(10, 4),
            Child = new TextBlock
            {
                Text = node.Type.Label,
                Foreground = Brushes.White,
                FontWeight = FontWeight.DemiBold,
                FontSize = 13,
            },
        };

        // input rows (left), output rows (right)
        var inputs = new StackPanel { Spacing = 4 };
        foreach (Port p in node.Type.Inputs)
            inputs.Children.Add(PortRow(node, p, isInput: true));

        var outputs = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,   // hug the right edge of its column
            Margin = new Thickness(24, 0, 0, 0),               // minimum gap from the inputs
        };
        foreach (Port p in node.Type.Outputs)
            outputs.Children.Add(PortRow(node, p, isInput: false));

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
        content.Children.Add(body);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2D2D30")),
            BorderBrush = Brushes.SteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),        // no padding: title bar + dots reach the edges
            ClipToBounds = false,              // let the port dots overflow past the border
            Tag = node,                        // visual -> model link
            Child = content,
        };
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
        _ => Brush("#455A64"),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

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
    private Control PortRow(NodeInstance node, Port port, bool isInput)
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
            Foreground = Brushes.LightGray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

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
        if (targetDot?.Tag is PortRef target && CanConnect(_wireSource, target))
        {
            // valid: snap the wire to the target, put it behind the nodes, and record it
            // non-variadic input: last wire wins -> drop any existing wire into the same port
            if (!target.Port.Variadic)
                foreach (Wire existing in _wires.Where(w => SamePort((PortRef)w.To.Tag!, target)).ToList())
                    RemoveWire(existing);

            _rubberBand!.Data = BezierGeometry(DotCenter(_wireSourceDot!), DotCenter(targetDot));
            NodeCanvas.Children.Remove(_rubberBand);
            NodeCanvas.Children.Insert(0, _rubberBand);   // index 0 = behind the node boxes
            _rubberBand.PointerPressed += OnWirePressed;  // clickable -> selectable/deletable

            _edges.Add(new Edge(_wireSource.Node.Id, _wireSource.Port.Name, target.Node.Id, target.Port.Name));
            _wires.Add(new Wire(_rubberBand, _wireSourceDot!, targetDot,
                                LocalOffset(_wireSourceDot!), LocalOffset(targetDot)));
        }
        else
        {
            NodeCanvas.Children.Remove(_rubberBand!);     // missed / invalid -> discard
        }

        if (_wireHoverDot is not null) { SetPortHover(_wireHoverDot, false); _wireHoverDot = null; }
        if (_wireSourceDot is not null) SetPortHover(_wireSourceDot, false);   // it was left hovered from the initial enter
        _wireSource = null;
        _wireSourceDot = null;
        _rubberBand = null;
        e.Pointer.Capture(null);
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
        SetPortEdge(dot, on ? Brushes.White : Brush("#1E1E1E"), on ? 3 : 1);

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

    // set the selection (node OR wire), restoring the previously selected one's look
    private void Select(Border? node, Wire? wire)
    {
        if (_selectedNode is not null) _selectedNode.BorderBrush = Brushes.SteelBlue;   // restore
        if (_selectedWire is not null) _selectedWire.Path.Stroke = WireColor(_selectedWire);
        _selectedNode = node;
        _selectedWire = wire;
        if (node is not null) node.BorderBrush = Brushes.White;                          // highlight
        if (wire is not null) wire.Path.Stroke = Brushes.White;

        // update the info panel + output for the selected node
        InfoPanel.Text = node?.Tag is NodeInstance ni
            ? $"{ni.Type.Label}\n{ni.Type.Kind}\n{ni.Type.Category} · {ni.Type.Tier}"
            : "(click a node)";
        RefreshOutput();
    }

    private void OnWirePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Path path) return;
        Wire? w = _wires.FirstOrDefault(x => ReferenceEquals(x.Path, path));
        if (w is not null) { Select(null, w); e.Handled = true; }
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, NodeCanvas)) Select(null, null);   // clicked empty canvas
    }

    private void RemoveWire(Wire w)
    {
        NodeCanvas.Children.Remove(w.Path);
        _wires.Remove(w);
        var src = (PortRef)w.From.Tag!;
        var tgt = (PortRef)w.To.Tag!;
        _edges.RemoveAll(edge => edge.SourceId == src.Node.Id && edge.SourceOut == src.Port.Name
                              && edge.TargetId == tgt.Node.Id && edge.TargetIn == tgt.Port.Name);
        if (ReferenceEquals(_selectedWire, w)) _selectedWire = null;
    }

    private void RemoveNode(Border box)
    {
        if (box.Tag is not NodeInstance node) return;
        // cascade: delete every wire touching this node (as source or target)
        foreach (Wire w in _wires.Where(w => DotNode(w.From) == node || DotNode(w.To) == node).ToList())
            RemoveWire(w);
        NodeCanvas.Children.Remove(box);
        _nodes.Remove(node);
        if (ReferenceEquals(_selectedNode, box)) _selectedNode = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedWire is not null) RemoveWire(_selectedWire);
            else if (_selectedNode is not null) RemoveNode(_selectedNode);
        }
        base.OnKeyDown(e);
    }

    // --- run: serialize the graph, send it to the backend, show results ---

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            // build the graph-doc: nodes with their kind + inputs (edges live on the target)
            var graphDoc = new
            {
                nodes = _nodes.Select(n => new { id = n.Id, kind = n.Type.Kind, inputs = InputsFor(n) }).ToList(),
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

    // --- output panel rendering ---

    private void RefreshOutput()
    {
        string? id = (_selectedNode?.Tag as NodeInstance)?.Id;
        OutputArea.Content = id is not null && _results.TryGetValue(id, out JsonElement payload)
            ? BuildOutput(payload)
            : new TextBlock { Text = "(Run the graph, then click a node)", Foreground = Brushes.Gray };
    }

    private static Control BuildOutput(JsonElement p)
    {
        if (p.TryGetProperty("error", out JsonElement err))
            return new TextBlock { Text = err.GetString(), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

        return p.GetProperty("type").GetString() switch
        {
            "frame" => BuildTable(p),
            "expr" => new TextBlock { Text = p.GetProperty("repr").GetString(), Foreground = Brushes.LightGray },
            _ => new TextBlock { Text = CellText(p.GetProperty("value")), Foreground = Brushes.LightGray },
        };
    }

    private static Control BuildTable(JsonElement p)
    {
        var cols = p.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        var grid = new Grid();
        foreach (var _ in cols) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        int row = 0;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < cols.Count; c++) AddCell(grid, cols[c], row, c, header: true);

        foreach (JsonElement r in p.GetProperty("rows").EnumerateArray())
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            int c = 0;
            foreach (JsonElement cell in r.EnumerateArray()) AddCell(grid, CellText(cell), row, c++, header: false);
        }
        return grid;
    }

    private static void AddCell(Grid grid, string text, int row, int col, bool header)
    {
        var tb = new TextBlock
        {
            Text = text,
            Margin = new Thickness(8, 3),
            FontWeight = header ? FontWeight.Bold : FontWeight.Normal,
            Foreground = header ? Brushes.White : Brushes.LightGray,
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
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
