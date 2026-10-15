using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;   // UniformGrid
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nota;

public partial class MainWindow : Window
{
    // --- column chips: params marked widget "column" / "columns" / "sort" ---
    // The param stays a plain string ("a, b" / "score desc, age") so saved graphs and the backend are
    // unchanged; the chips are just an editor for it. Suggestions come from the table wired into the
    // node (`input_schema` RPC, fetched when the add box gets focus). dtypes are remembered per node
    // and refreshed from the run payloads after every Run -- no continuous fetching.

    private readonly Dictionary<string, Dictionary<string, string>> _colTypes = new();   // node id -> column -> dtype
    private readonly Dictionary<(string Node, string Param), Action> _chipViews = new(); // repaint callbacks

    // dtype family -> light fill + border (black name / dark-gray type text read on all of them, both themes).
    // One palette for the chips and the legend's "Data types" tab; wire/port colors are separate (TypeColor).
    private static readonly (string Family, string Fill, string Border, string Dtypes)[] DtypeFamilies =
    {
        ("Integer",     "#BBDEFB", "#64B5F6", "Int8 … Int64, UInt8 … UInt64"),
        ("Float",       "#B2EBF2", "#4DD0E1", "Float32, Float64, Decimal"),
        ("Text",        "#FFE0B2", "#FFB74D", "String"),
        ("Boolean",     "#E1BEE7", "#BA68C8", "Boolean"),
        ("Date / time", "#FFF59D", "#FDD835", "Date, Datetime, Time, Duration"),
        ("Category",    "#F8BBD0", "#F06292", "Categorical, Enum"),
        ("Nested",      "#CFD8DC", "#90A4AE", "List, Array, Struct"),
    };

    // index into DtypeFamilies for a polars dtype string ("Int64", "Datetime(time_unit='us', …)"); -1 = unknown
    private static int DtypeFamily(string? dtype)
    {
        if (string.IsNullOrEmpty(dtype)) return -1;
        bool Is(params string[] prefixes) => prefixes.Any(p => dtype.StartsWith(p, StringComparison.Ordinal));
        if (Is("Int", "UInt")) return 0;
        if (Is("Float", "Decimal")) return 1;
        if (Is("String", "Utf8")) return 2;
        if (Is("Boolean")) return 3;
        if (Is("Date", "Time", "Duration")) return 4;
        if (Is("Categorical", "Enum")) return 5;
        if (Is("List", "Array", "Struct")) return 6;
        return -1;
    }

    private static bool IsChipWidget(ParamSpec p) => p.Widget is "column" or "columns" or "sort";

    private sealed record ColChip(string Name, bool Desc);

    private static List<ColChip> ParseChips(string? text, bool sort)
    {
        var chips = new List<ColChip>();
        foreach (string raw in (text ?? "").Split(','))
        {
            string item = raw.Trim();
            if (item.Length == 0) continue;
            bool desc = false;
            int sp = item.LastIndexOf(' ');
            if (sort && sp > 0)
            {
                string tail = item[(sp + 1)..];
                if (tail.Equals("desc", StringComparison.OrdinalIgnoreCase) || tail.Equals("asc", StringComparison.OrdinalIgnoreCase))
                {
                    desc = tail.Equals("desc", StringComparison.OrdinalIgnoreCase);
                    item = item[..sp].Trim();
                }
            }
            chips.Add(new ColChip(item, desc));
        }
        return chips;
    }

    private static string FormatChips(List<ColChip> chips, bool sort) =>
        string.Join(", ", chips.Select(c => sort && c.Desc ? c.Name + " desc" : c.Name));

    // ordered (column, dtype) of the table(s) wired into this node; also remembered for its chips
    private List<KeyValuePair<string, string>> FetchInputSchema(NodeInstance node)
    {
        var cols = new List<KeyValuePair<string, string>>();
        try
        {
            JsonElement? r = BackendRequest("input_schema", new { graph = BuildGraphDoc(), node_id = node.Id });
            if (r is not null && r.Value.TryGetProperty("result", out JsonElement res) && res.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>();
                foreach (JsonProperty port in res.EnumerateObject())
                    foreach (JsonProperty c in port.Value.EnumerateObject())
                        if (seen.Add(c.Name)) cols.Add(new(c.Name, c.Value.GetString() ?? ""));
            }
        }
        catch { /* suggestions are best-effort */ }
        if (cols.Count > 0)
        {
            _colTypes[node.Id] = cols.ToDictionary(c => c.Key, c => c.Value);
            RepaintChips(node.Id);
        }
        return cols;
    }

    private void RepaintChips(string nodeId)
    {
        foreach (var kv in _chipViews.Where(kv => kv.Key.Node == nodeId).ToList()) kv.Value();
    }

    // The editor: label, a 3-column grid of chips (name in black, dtype below in gray), and an add box.
    private Control ChipWidget(NodeInstance node, ParamSpec p, Action? refreshLayout)
    {
        bool multi = p.Widget != "column";
        bool sort = p.Widget == "sort";
        List<ColChip> chips = ParseChips(node.Params[p.Name]?.ToString(), sort);

        var label = new TextBlock { Text = p.Name, FontSize = 11, Foreground = ThemeSubtext };
        ToolTip.SetTip(label, DocTip(p.Doc, p.Name));

        var grid = new UniformGrid { Columns = 3 };
        var add = new AutoCompleteBox
        {
            FontSize = 11, MinWidth = 0, MinHeight = 24,
            Background = ThemeField, Foreground = ThemeText,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,
            PlaceholderText = multi ? "+ add column" : "choose column",
        };
        add.Styles.Add(new Style(s => s.OfType<AutoCompleteBox>().Template().OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Setter(TextBox.PaddingProperty, new Thickness(6, 1)),
            },
        });

        void Commit() => node.Params[p.Name] = FormatChips(chips, sort);

        void Render()
        {
            grid.Children.Clear();
            _colTypes.TryGetValue(node.Id, out Dictionary<string, string>? types);
            foreach (ColChip c in chips.ToList())
            {
                string? dtype = types is not null && types.TryGetValue(c.Name, out string? t) ? t : null;
                bool missing = types is { Count: > 0 } && dtype is null;   // known table, column not in it
                grid.Children.Add(MakeChip(c, dtype, missing));
            }
            grid.IsVisible = chips.Count > 0;
            add.IsVisible = multi || chips.Count == 0;
        }

        Control MakeChip(ColChip c, string? dtype, bool missing)
        {
            var name = new TextBlock
            {
                Text = c.Name + (sort ? (c.Desc ? " ↓" : " ↑") : ""),
                FontSize = 11, Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var type = new TextBlock
            {
                Text = missing ? "not in table" : dtype ?? " ",   // " " keeps the chip height stable
                FontSize = 9, Foreground = missing ? Brush("#C62828") : Brush("#555555"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var remove = new TextBlock
            {
                Text = "×", FontSize = 12, Foreground = Brush("#444444"),
                Margin = new Thickness(4, -2, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(remove, "remove");
            remove.PointerPressed += (_, e) =>
            {
                e.Handled = true;   // don't start a node drag
                Snapshot();
                chips.Remove(c);
                Commit(); Render(); refreshLayout?.Invoke();
            };
            var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var texts = new StackPanel { Children = { name, type } };
            Grid.SetColumn(texts, 0);
            Grid.SetColumn(remove, 1);
            inner.Children.Add(texts);
            inner.Children.Add(remove);

            int fam = DtypeFamily(dtype);   // -1 (unknown / not fetched / missing) -> neutral white 50 %
            var chip = new Border
            {
                Background = fam >= 0 ? Brush(DtypeFamilies[fam].Fill) : Brush("#80FFFFFF"),
                BorderBrush = missing ? Brush("#C62828")
                            : fam >= 0 ? Brush(DtypeFamilies[fam].Border) : Brush("#33000000"),   // edge visible on light themes too
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 2),
                Margin = new Thickness(1.5),
                MinWidth = 58, MaxWidth = 110,
                Child = inner,
            };
            ToolTip.SetTip(chip, missing ? $"{c.Name}: not in the connected table" : dtype is null ? c.Name : $"{c.Name} · {dtype}");
            if (sort)   // click flips ascending / descending
            {
                chip.Cursor = new Cursor(StandardCursorType.Hand);
                chip.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    Snapshot();
                    int i = chips.IndexOf(c);
                    if (i >= 0) chips[i] = c with { Desc = !c.Desc };
                    Commit(); Render();
                };
            }
            return chip;
        }

        void AddColumn(string text)
        {
            string n = text.Trim();
            if (n.Length == 0 || chips.Any(c => c.Name == n)) return;
            Snapshot();
            if (!multi) chips.Clear();
            chips.Add(new ColChip(n, false));
            Commit(); Render(); refreshLayout?.Invoke();
        }

        add.GotFocus += (_, _) =>
        {
            List<KeyValuePair<string, string>> cols = FetchInputSchema(node);
            List<string> left = cols.Select(c => c.Key).Where(n => chips.All(c => c.Name != n)).ToList();
            add.ItemsSource = left;
            add.PlaceholderText = cols.Count > 0 ? (multi ? "+ add column" : "choose column")
                : HasFrameInput(node) ? "connect a table to see its columns"
                : "wire this into a node that has a table to see its columns";   // expression nodes take no table themselves
            if (left.Count > 0)   // post the open: setting it inside GotFocus is overridden by the control's own focus handling
                Avalonia.Threading.Dispatcher.UIThread.Post(() => add.IsDropDownOpen = true);
        };
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedItem is not string picked || picked.Length == 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                add.SelectedItem = null;
                add.Text = "";
                AddColumn(picked);
            });
        };
        add.KeyDown += (_, e) =>   // a name that isn't in the list (not yet existing, or no table wired): Enter adds it as typed
        {
            if (e.Key != Key.Enter || add.IsDropDownOpen || string.IsNullOrWhiteSpace(add.Text)) return;
            string typed = add.Text!;
            add.Text = "";
            AddColumn(typed);
            e.Handled = true;
        };

        _chipViews[(node.Id, p.Name)] = Render;
        Render();
        return new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2), Children = { label, grid, add } };
    }

    // --- refresh when wiring changes: a Column node wired into a Filter sees its table right away ---

    private bool _suspendChipRefresh;   // true during load / paste / delete cascades (wires are added in bulk)

    private void WithoutChipRefresh(Action bulk)
    {
        bool was = _suspendChipRefresh;
        _suspendChipRefresh = true;
        try { bulk(); } finally { _suspendChipRefresh = was; }
    }

    // A wire into `targetId` was added or removed. Affected chip nodes: the target itself and the
    // expression chain feeding it (edges into non-frame ports) -- upstream tables are unchanged.
    private void RefreshChipsAfterWire(string targetId)
    {
        if (_suspendChipRefresh) return;
        var affected = new HashSet<string> { targetId };
        var stack = new Stack<string>();
        stack.Push(targetId);
        while (stack.Count > 0)
        {
            string id = stack.Pop();
            NodeInstance? n = _nodes.FirstOrDefault(x => x.Id == id);
            if (n is null) continue;
            foreach (Edge e in _edges.Where(e => e.TargetId == id))
            {
                bool frameIn = n.Type.Inputs.Any(i => i.Name == e.TargetIn && i.Type == "frame");
                if (!frameIn && affected.Add(e.SourceId)) stack.Push(e.SourceId);
            }
        }
        foreach (NodeInstance n in _nodes.Where(n => affected.Contains(n.Id) && n.Type.Params.Any(IsChipWidget)).ToList())
            if (FetchInputSchema(n).Count == 0)
            {
                _colTypes.Remove(n.Id);   // disconnected: forget the types, chips go neutral
                RepaintChips(n.Id);
            }
    }

    // --- dtype refresh after a Run: the payloads already carry each table's schema ---

    private bool HasFrameInput(NodeInstance n) => n.Type.Inputs.Any(i => i.Type == "frame");

    // ids of the table node(s) wired into `node`'s frame inputs (expression nodes: into their consumer's)
    private List<string> ConnectedFrameSources(NodeInstance node)
    {
        NodeInstance? Get(string id) => _nodes.FirstOrDefault(n => n.Id == id);

        List<Edge> FrameEdges(NodeInstance n) =>
            _edges.Where(e => e.TargetId == n.Id && n.Type.Inputs.Any(i => i.Name == e.TargetIn && i.Type == "frame")).ToList();

        string TableOf(string id)   // a non-table output (group_by) passes through to the table it was built from
        {
            for (int hop = 0; hop < 20; hop++)
            {
                NodeInstance? n = Get(id);
                if (n is null || n.Type.Outputs.Count == 0 || n.Type.Outputs[0].Type == "frame") break;
                List<Edge> up = FrameEdges(n);
                if (up.Count == 0) break;
                id = up[0].SourceId;
            }
            return id;
        }

        if (HasFrameInput(node)) return FrameEdges(node).Select(e => TableOf(e.SourceId)).ToList();

        var queue = new Queue<string>(_edges.Where(e => e.SourceId == node.Id).Select(e => e.TargetId));
        var seen = new HashSet<string> { node.Id };
        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            if (!seen.Add(id) || Get(id) is not NodeInstance x) continue;
            if (HasFrameInput(x)) return FrameEdges(x).Select(e => TableOf(e.SourceId)).ToList();
            foreach (Edge e in _edges.Where(e => e.SourceId == id)) queue.Enqueue(e.TargetId);
        }
        return new List<string>();
    }

    // after a Run: names stay as chosen, dtypes (and the "not in table" flag) follow the new results
    private void RefreshChipTypes()
    {
        foreach (NodeInstance n in _nodes.Where(n => n.Type.Params.Any(IsChipWidget)))
        {
            var merged = new Dictionary<string, string>();
            foreach (string src in ConnectedFrameSources(n))
                if (_results.TryGetValue(src, out JsonElement payload)
                    && payload.TryGetProperty("schema", out JsonElement sch) && sch.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty c in sch.EnumerateObject())
                        merged.TryAdd(c.Name, c.Value.GetString() ?? "");
            if (merged.Count > 0) _colTypes[n.Id] = merged;
        }
        var ids = _nodes.Select(n => n.Id).ToHashSet();
        foreach (var kv in _chipViews.ToList())
            if (ids.Contains(kv.Key.Node)) kv.Value(); else _chipViews.Remove(kv.Key);
    }
}
