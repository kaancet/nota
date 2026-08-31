using System.Collections.Generic;

namespace Nota;

// One input or output port on a node type (from the manifest).
public record Port(string Name, string Type, bool Variadic, bool Optional, string Doc = "");

// One parameter (widget) on a node type (from the manifest).
// Choices (enum) -> ComboBox. Widget (e.g. "kvlist") -> a special editor instead of a textbox.
public record ParamSpec(string Name, string Type, object? Default, bool Required,
                        string Doc = "", List<string>? Choices = null, string? Widget = null);

// A node kind from the manifest. Carries everything the palette, the ports,
// the params, and the info panel need.
public record NodeType(
    string Kind,
    string Label,
    string Category,
    string Tier,
    string Doc,
    List<Port> Inputs,
    List<Port> Outputs,
    List<ParamSpec> Params,
    string Examples = "",
    string? DocUrl = null);


// Identifies one specific port on one placed node. Stored on each port dot's Tag,
// so a pointer event on a dot knows which node + port (and which side) it is.
public record PortRef(NodeInstance Node, Port Port, bool IsInput);

// A connection in the frontend graph: source node's out-port -> target node's in-port.
// Mirrors the backend graph-doc edge; SourceOut/TargetIn are the real Port.Name values.
public record Edge(string SourceId, string SourceOut, string TargetId, string TargetIn);

// A node PLACED on the canvas: one instance of a NodeType, with its own id + position.
public class NodeInstance
{
    public string Id { get; }          // get-only: set in the constructor, never changed
    public NodeType Type { get; }      // get-only
    public double X { get; set; }      // get + set: mutable (dragging updates it)
    public double Y { get; set; }
    public bool Collapsed { get; set; }   // node body hidden to just title + port stubs (#6)

    // live param values (name -> value), seeded from the type's defaults, edited by widgets
    public Dictionary<string, object?> Params { get; } = new();

    public NodeInstance(string id, NodeType type, double x, double y)
    {
        Id = id;
        Type = type;
        X = x;
        Y = y;
        foreach (ParamSpec p in type.Params)
            Params[p.Name] = p.Default;
    }
}
