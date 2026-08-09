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

/// <summary>
/// A typed value — a number, a boolean, null, a GUID, a blob.
/// </summary>
/// <param name="Type">The frame's Type byte.</param>
/// <param name="Payload">The payload exactly as it appears on the wire.</param>
/// <remarks>
/// <para>
/// One record for every primitive rather than one per kind. A record per kind reads better
/// at a call site, but it multiplies the switch in three encoder passes and two decoder
/// methods by the number of types, and every one of those cases would say the same thing:
/// write the Type byte, write the payload. Typed access lives in <see cref="Primitives"/>
/// instead, where it costs nothing structural.
/// </para>
/// <para>
/// The payload is kept as raw bytes rather than a decoded value so that re-encoding is
/// byte-exact by construction rather than by care.
/// </para>
/// </remarks>
public sealed record PrimitiveNode(byte Type, ReadOnlyMemory<byte> Payload) : Node
{
    /// <summary>Compares the payload by content.</summary>
    /// <param name="other">Node to compare with.</param>
    /// <returns><see langword="true"/> if both hold the same bytes under the same type.</returns>
    /// <remarks>
    /// A record's generated equality would compare <see cref="ReadOnlyMemory{T}"/> by its
    /// underlying object, offset and length, so two nodes holding identical bytes in separate
    /// arrays would be unequal. Nothing here is a reference type to the caller — this is a
    /// value — and the encoder relies on it to recognise a repeated primitive.
    /// </remarks>
    public bool Equals(PrimitiveNode? other) =>
        other is not null && Type == other.Type && Payload.Span.SequenceEqual(other.Payload.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Type);
        hash.AddBytes(Payload.Span);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A frame whose shape was understood but whose type was not.
/// </summary>
/// <param name="Type">The frame's Type byte.</param>
/// <param name="Payload">The payload exactly as it appears on the wire.</param>
/// <remarks>
/// Kept so that a document using a type allocated after this reader was written passes
/// through it unchanged instead of being rejected. Deliberately a separate record from
/// <see cref="PrimitiveNode"/>: "I did not understand this" must not be silently mistaken
/// for a value, and a <c>switch</c> that omits this case fails loudly.
/// </remarks>
public sealed record UnknownNode(byte Type, ReadOnlyMemory<byte> Payload) : Node
{
    /// <summary>Compares the payload by content.</summary>
    /// <param name="other">Node to compare with.</param>
    /// <returns><see langword="true"/> if both hold the same bytes under the same type.</returns>
    /// <remarks>Same reasoning as <see cref="PrimitiveNode.Equals(PrimitiveNode)"/>.</remarks>
    public bool Equals(UnknownNode? other) =>
        other is not null && Type == other.Type && Payload.Span.SequenceEqual(other.Payload.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Type);
        hash.AddBytes(Payload.Span);
        return hash.ToHashCode();
    }
}
