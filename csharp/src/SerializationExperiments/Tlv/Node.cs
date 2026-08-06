namespace SerializationExperiments.Tlv;

/// <summary>
/// A node in an XML-like document: elements and text only, no attributes.
/// </summary>
public abstract record Node;

/// <summary>
/// An element with a name and ordered children.
/// </summary>
/// <param name="Name">Tag name. Interned on first occurrence in document order.</param>
/// <param name="Children">Ordered children; may be empty.</param>
public sealed record ElementNode(string Name, IReadOnlyList<Node> Children) : Node;

/// <summary>
/// A run of character data.
/// </summary>
/// <param name="Value">Text content; may be empty.</param>
public sealed record TextNode(string Value) : Node;
