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
    // --- selection + deletion ---

    // outward highlight ring for a selected node (Spread outward, Blur 0 -> crisp; no layout reflow)
    private static readonly BoxShadows SelGlow =
        new(new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 0, Spread = 2.5, Color = Color.Parse("#00b3a7") });

    private static NodeInstance DotNode(Shape dot) => ((PortRef)dot.Tag!).Node;
    private static bool SamePort(PortRef a, PortRef b) => ReferenceEquals(a.Node, b.Node) && a.Port.Name == b.Port.Name;
    private static IBrush WireColor(Wire w) => TypeColor(((PortRef)w.From.Tag!).Port.Type);

    // clear every selected node/wire back to its normal look
    private void ClearSelection()
    {
        foreach (Border b in _selNodes) { b.BorderBrush = Brushes.CadetBlue; b.BoxShadow = default; }
        foreach (Wire w in _selWires) w.Path.Stroke = WireColor(w);
        foreach (Border bx in _selBoxes)
        {
            if (bx.Tag is TextNote) { bx.BorderThickness = new Thickness(0); bx.BorderBrush = Brushes.Transparent; }
            else bx.BorderThickness = new Thickness(1.5);
        }
        _selNodes.Clear();
        _selWires.Clear();
        _selBoxes.Clear();
    }

    // select a node; additive (shift) toggles it into/out of the current set
    private void SelectNode(Border box, bool additive)
    {
        if (!additive) ClearSelection();
        if (additive && _selNodes.Remove(box)) { box.BorderBrush = Brushes.SteelBlue; box.BoxShadow = default; }   // toggle off
        else { _selNodes.Add(box); box.BorderBrush = ThemeAccent; box.BoxShadow = SelGlow; }
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

    // center the viewport on the center-of-mass of all placed nodes
    private void CenterOnNodes()
    {
        if (_nodes.Count == 0) return;
        double cx = 0, cy = 0;
        foreach (NodeInstance n in _nodes)
        {
            Border? box = NodeCanvas.Children.OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.Tag, n));
            double w = box?.Bounds.Width ?? 150;
            double h = box?.Bounds.Height ?? 80;
            cx += n.X + w / 2;
            cy += n.Y + h / 2;
        }
        cx /= _nodes.Count;
        cy /= _nodes.Count;
        double vw = CanvasViewport.Bounds.Width;
        double vh = CanvasViewport.Bounds.Height;
        _pan.X = vw / 2 - _zoom.ScaleX * cx;
        _pan.Y = vh / 2 - _zoom.ScaleY * cy;
        UpdateWires();
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
        // middle-drag OR Ctrl/Cmd+left-drag pans (trackpad users have no middle button)
        bool ctrlPan = props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (props.IsMiddleButtonPressed || ctrlPan)
        {
            _panning = true;
            _panStart = e.GetPosition(CanvasViewport);
            _panOrigin = new Point(_pan.X, _pan.Y);
            e.Pointer.Capture(CanvasViewport);
            e.Handled = true;
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
            if (!(_drawingBox.Width >= 20 && _drawingBox.Height >= 20))   // ignore accidental click / tiny drag (NaN when no move)
                NodeCanvas.Children.Remove(_drawingBox);
            else ShowBoxMenu(_drawingBox, _boxColorIdx);
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
                    { b.BorderBrush = ThemeAccent; b.BoxShadow = SelGlow; }
            SyncPanels();
        }
        e.Pointer.Capture(null);
    }

    // right-drag released a valid rectangle -> let the user choose group or text note
    private void ShowBoxMenu(Border box, int colorIdx)
    {
        var groupBtn = new Border
        {
            Padding = new Thickness(8, 4), Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            Child = new TextBlock { Text = "Group Region", Foreground = ThemeText, FontSize = 13 },
        };
        var textBtn = new Border
        {
            Padding = new Thickness(8, 4), Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            Child = new TextBlock { Text = "Text Note", Foreground = ThemeText, FontSize = 13 },
        };

        var popup = new Popup
        {
            PlacementTarget = NodeCanvas, Placement = PlacementMode.Pointer, IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = ThemeNodeBg, BorderBrush = ThemeAccent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(2),
                Child = new StackPanel { Children = { groupBtn, textBtn } },
            },
        };

        groupBtn.PointerPressed += (_, _) => { FinalizeBox(box, colorIdx); _boxColorIdx++; popup.IsOpen = false; };
        textBtn.PointerPressed += (_, _) => { FinalizeTextNote(box); popup.IsOpen = false; };
        popup.Closed += (_, _) =>
        {
            NodeCanvas.Children.Remove(popup);
            if (box.Tag is not GroupBox and not TextNote)
                NodeCanvas.Children.Remove(box);
        };

        NodeCanvas.Children.Add(popup);
        popup.Open();
    }

    // turn the just-drawn rectangle into a real group box with a watermark title + small color swatch
    private void FinalizeBox(Border box, int colorIdx)
    {
        var gb = new GroupBox { ColorIdx = colorIdx };
        box.Tag = gb;
        Color c = _boxColors[colorIdx % _boxColors.Length];

        gb.Swatch = new Button
        {
            Width = 14, Height = 14, Padding = new Thickness(0),
            Background = new SolidColorBrush(c), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        gb.Swatch.Click += (_, _) => SetBoxColor(box, (gb.ColorIdx + 1) % _boxColors.Length);

        gb.Name = new TextBox
        {
            PlaceholderText = "group", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(150, 180, 180, 180)),
            FontWeight = FontWeight.Bold, FontSize = 28,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
            IsHitTestVisible = false,
        };
        gb.Name.LostFocus += (_, _) => gb.Name.IsHitTestVisible = false;

        // scale font with box size: min 15, max 40
        void ScaleFont()
        {
            double dim = Math.Min(box.Bounds.Width, box.Bounds.Height);
            gb.Name.FontSize = Math.Clamp(dim / 5.0, 15, 40);
        }
        box.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) ScaleFont(); };
        box.LayoutUpdated += (_, _) => ScaleFont();

        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(gb.Name);   // watermark at bottom
        grid.Children.Add(gb.Swatch); // swatch top-left on top
        box.Child = grid;
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

    private void FinalizeTextNote(Border box)
    {
        box.Background = Brushes.Transparent;
        box.BorderBrush = Brushes.Transparent;
        box.BorderThickness = new Thickness(0);

        var tn = new TextNote();
        box.Tag = tn;

        tn.Text = new TextBox
        {
            PlaceholderText = "note", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = ThemeText, FontSize = 15, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            IsHitTestVisible = true,
        };
        tn.Text.LostFocus += (_, _) => tn.Text.IsHitTestVisible = false;

        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(tn.Text);
        box.Child = grid;
        box.PointerPressed += OnBoxPressed;
    }

    private Border AddTextNote(double x, double y, double w, double h, string text)
    {
        var box = new Border
        {
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0), ZIndex = -1, Width = w, Height = h,
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        NodeCanvas.Children.Add(box);
        FinalizeTextNote(box);
        var tn = (TextNote)box.Tag!;
        tn.Text.Text = text;
        tn.Text.IsHitTestVisible = false;
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
        if (additive && _selBoxes.Remove(box))
        {
            if (box.Tag is TextNote) { box.BorderThickness = new Thickness(0); box.BorderBrush = Brushes.Transparent; }
            else box.BorderThickness = new Thickness(1.5);
        }
        else
        {
            _selBoxes.Add(box);
            if (box.Tag is TextNote) { box.BorderThickness = new Thickness(1); box.BorderBrush = ThemeAccent; }
            else box.BorderThickness = new Thickness(3);
        }
        SyncPanels();
    }

    private void OnBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border box) return;
        if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
        if (e.Source is TextBox or Button) return;   // editing the name / cycling color, not a drag
        if (e.ClickCount == 2 && box.Tag is GroupBox gb2)
        {
            gb2.Name.IsHitTestVisible = true;
            gb2.Name.Focus();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && box.Tag is TextNote tn2)
        {
            tn2.Text.IsHitTestVisible = true;
            tn2.Text.Focus();
            e.Handled = true;
            return;
        }
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

}
