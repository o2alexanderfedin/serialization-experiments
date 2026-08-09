namespace SerializationExperiments.Tlv;

/// <summary>
/// Decoder behaviour that the wire format does not dictate.
/// </summary>
/// <remarks>
/// These change what the decoder builds, never what it accepts: the same bytes decode to
/// equal content under any settings.
/// </remarks>
public sealed record TlvDecoderOptions
{
    /// <summary>Shared instance of the defaults.</summary>
    public static TlvDecoderOptions Default { get; } = new();

    /// <summary>
    /// Whether repeated values decode to one shared string instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="true"/> (default) hands back the instance the first occurrence
    /// produced, so a reference costs no UTF-8 decode and no allocation. Safe because
    /// strings are immutable — the sharing is unobservable except through
    /// <see cref="object.ReferenceEquals"/> — and it is what makes interning fast on the
    /// decode side. The right choice for RPC, where values are transient data.
    /// </para>
    /// <para>
    /// <see langword="false"/> materialises a distinct instance per occurrence. Choose it
    /// when something downstream attaches meaning to reference identity — an identity map,
    /// a cache keyed by reference, or interop that mutates in place. Note that a
    /// <c>TEXT_REF</c> asserts only that two values are *equal*, never that they are the
    /// same object, so neither setting is more faithful to the document; this decides what
    /// your object model wants.
    /// </para>
    /// <para>
    /// Empty strings are unaffected either way: <see cref="string.Empty"/> is a runtime
    /// singleton, and values under two bytes are never referenced in the first place.
    /// </para>
    /// </remarks>
    public bool ShareValueInstances { get; init; } = true;

    /// <summary>
    /// Whether type-tagged frames are accepted at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="true"/> (default) decodes a tagged frame to a
    /// <see cref="TypedNode"/> carrying the name as text. That is the whole of it — the
    /// decoder does not resolve the name, so accepting one costs nothing but a wrapper node,
    /// and an unrecognised name re-encodes unchanged instead of failing. Erroring on names
    /// it does not know would ossify the format: every new derived type would break every
    /// existing reader.
    /// </para>
    /// <para>
    /// <see langword="false"/> rejects them outright, for callers who want the guarantee that
    /// a document contains only elements and text — a peer that has no notion of types
    /// should be able to say so rather than silently accept frames it will never look at.
    /// </para>
    /// <para>
    /// Note what neither setting does: turn a name into a <see cref="System.Type"/>. Mapping
    /// names to types is the caller's, in caller code, against a list the caller wrote.
    /// </para>
    /// </remarks>
    public bool AllowTypeNames { get; init; } = true;

    /// <summary>
    /// Whether frames of an unrecognised type are carried through rather than refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="true"/> (default) decodes such a frame to an <see cref="UnknownNode"/>
    /// holding its Type byte and payload, which re-encodes to exactly the bytes it came from.
    /// The shape nibble makes this possible without understanding the type at all. Refusing
    /// instead would ossify the format: every type allocated after a reader was built would
    /// break it, and RFC 8949 makes the same point about unknown CBOR tags.
    /// </para>
    /// <para>
    /// The frame is carried, never interpreted — and <see cref="UnknownNode"/> is a distinct
    /// record, so code that does not handle it fails loudly instead of mistaking it for a
    /// value.
    /// </para>
    /// <para>
    /// <see langword="false"/> refuses them, for a peer that should only ever receive types
    /// it knows. Shapes that carry no width are refused either way, since nothing can step
    /// over them.
    /// </para>
    /// </remarks>
    public bool AllowUnknownTypes { get; init; } = true;
}
