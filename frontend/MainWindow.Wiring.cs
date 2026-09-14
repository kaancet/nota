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

}
