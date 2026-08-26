using System.Collections.Generic;

namespace Nota;

// One input or output port on a node type (from the manifest).
public record Port(string Name, string Type, bool Variadic, bool Optional);

// A node kind from the manifest. Carries everything the palette, the ports
// and the info panel will need. Category/Tier aren't displayed yet,
// but we keep them now so the palette-grouping + color-coding ideas are cheap later.
public record NodeType(
    string Kind,
    string Label,
    string Category,
    string Tier,
    List<Port> Inputs,
    List<Port> Outputs);


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

    public NodeInstance(string id, NodeType type, double x, double y)
    {
        Id = id;
        Type = type;
        X = x;
        Y = y;
    }
}
