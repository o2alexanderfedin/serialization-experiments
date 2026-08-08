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

/// <summary>
/// A node tagged with the name of the type it came from.
/// </summary>
/// <param name="TypeName">
/// Caller-chosen discriminator. Interned on first occurrence, in its own id space.
/// </param>
/// <param name="Inner">The node carrying the actual content.</param>
/// <remarks>
/// <para>
/// This is the whole of the format's support for polymorphism, and it is deliberately
/// nothing more than a label. The codec never maps a name to a <see cref="System.Type"/>,
/// never loads an assembly, and never constructs anything: decoding a tagged frame produces
/// a <see cref="TypedNode"/> carrying the name as text, and resolving that name is the
/// caller's job, in caller code.
/// </para>
/// <para>
/// That boundary is the point. Formats that resolve embedded type names for you have a poor
/// record — <c>BinaryFormatter</c> was removed from .NET 9 over it, and Avro shipped three
/// successive versions of one allow-list before it held. A decoder that cannot name a type
/// cannot be talked into instantiating one.
/// </para>
/// <para>
/// Documents that never use this node encode exactly as they did before it existed: it has
/// its own frame type, so nothing else on the wire changes, and it draws its ids from a
/// table of its own rather than sharing the element-name or value spaces.
/// </para>
/// </remarks>
public sealed record TypedNode(string TypeName, Node Inner) : Node;
