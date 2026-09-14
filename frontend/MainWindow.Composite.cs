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
            JsonElement? cr = BackendRequest("create_node", new { definition });
            if (cr is null) return;
            JsonElement resp = cr.Value;
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

}
