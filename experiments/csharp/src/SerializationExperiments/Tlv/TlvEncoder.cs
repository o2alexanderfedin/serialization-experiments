using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace SerializationExperiments.Tlv;

/// <summary>
/// Encodes a <see cref="Node"/> tree to TLV in two passes, without buffering the value.
/// </summary>
/// <remarks>
/// <para>
/// TLV puts Length before Value, but a node's size is unknown until its subtree has been
/// walked. Rather than serialize into a buffer and measure it, the tree is walked twice: a
/// measuring pass computes every node's value length, then an emit pass writes headers from
/// those cached sizes. Peak memory is the recursion depth plus one long per node, never the
/// payload.
/// </para>
/// <para>
/// Both passes intern element names and text values from empty tables, in first-occurrence
/// document order. The measuring pass therefore is not pure arithmetic — whether a node
/// costs a literal or a short reference depends on the tables — and the emit pass must
/// start from fresh tables or it would write references where the measuring pass counted
/// literals.
/// </para>
/// </remarks>
public static class TlvEncoder
{
    /// <summary>
    /// Shortest value worth referencing.
    /// </summary>
    /// <remarks>
    /// Over k occurrences of an L-byte value the saving is (k-1)(L-1): strictly positive
    /// from L=2, exactly zero at L=1, and negative at L=0 where a 2-byte empty literal
    /// would be replaced by a 3-byte reference. Paired with the occurrence test in
    /// <see cref="Tables.ClaimsId"/>: a value must clear both to claim an id. The rule lives
    /// only here — the decoder learns the outcome from the type code, so tuning it is not a
    /// format change.
    /// </remarks>
    private const int MinInternedValueLength = 2;

    /// <summary>
    /// Longest UTF-8 literal encoded through the stack rather than a pooled array.
    /// </summary>
    /// <remarks>
    /// Covers every element name and the overwhelming majority of text values without
    /// touching the pool. <see cref="WriteUtf8"/> is never inlined — a method containing
    /// <c>stackalloc</c> cannot be — so the space is reclaimed on return rather than
    /// accumulating across the encoder's recursion.
    /// </remarks>
    private const int MaxStackUtf8 = 256;

    /// <summary>Encodes <paramref name="root"/> to a new array.</summary>
    /// <param name="root">Tree to encode.</param>
    /// <returns>The encoded document.</returns>
    /// <remarks>
    /// The measuring pass already knows the exact output length, so the array is allocated
    /// once at that size. Writing through a growable buffer instead would pay for a doubling
    /// chain and a final copy to hand back a right-sized array.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The document is larger than the largest possible array.
    /// </exception>
    public static byte[] Encode(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);

        List<long> sizes = [];
        Dictionary<string, int> occurrences = CountValues(root);
        long total = Measure(root, new Tables(occurrences), sizes, depth: 0);

        if (total > Array.MaxLength)
        {
            throw new InvalidOperationException(
                $"Document measures {total} bytes, past the {Array.MaxLength}-byte array limit. " +
                $"Encode into a stream sink instead.");
        }

        byte[] result = new byte[total];
        EmitMeasured(root, new BufferSink(result), sizes, total, occurrences);
        return result;
    }

    /// <summary>Encodes <paramref name="root"/> into <paramref name="sink"/>.</summary>
    /// <param name="root">Tree to encode.</param>
    /// <param name="sink">Destination.</param>
    /// <returns>Bytes written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The emit pass disagreed with the measuring pass. This means the two passes diverged —
    /// a corrupt document rather than a recoverable error — so it fails loudly here instead
    /// of reaching a decoder.
    /// </exception>
    public static long Encode(Node root, IByteSink sink)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(sink);

        List<long> sizes = [];
        Dictionary<string, int> occurrences = CountValues(root);
        long expected = Measure(root, new Tables(occurrences), sizes, depth: 0);
        return EmitMeasured(root, sink, sizes, expected, occurrences);
    }

    /// <summary>
    /// Runs the emit pass and checks it against what the measuring pass counted.
    /// </summary>
    private static long EmitMeasured(
        Node root,
        IByteSink sink,
        List<long> sizes,
        long expected,
        Dictionary<string, int> occurrences)
    {
        long before = sink.BytesWritten;
        int cursor = 0;

        // Fresh tables: the emit pass must rediscover names and values in the same order the
        // measuring pass did, or every length already counted would be wrong.
        Emit(root, sink, sizes, ref cursor, new Tables(occurrences));

        long written = sink.BytesWritten - before;
        if (written != expected)
        {
            throw new InvalidOperationException(
                $"Encoder passes disagree: measured {expected} bytes, wrote {written}.");
        }

        return written;
    }

    /// <summary>Computes the encoded size of <paramref name="root"/> without producing bytes.</summary>
    /// <param name="root">Tree to measure.</param>
    /// <returns>Total frame size in bytes, header included.</returns>
    /// <exception cref="ArgumentException">
    /// The tree nests deeper than <see cref="TlvLimits.MaxDepth"/>.
    /// </exception>
    public static long Measure(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Measure(root, new Tables(CountValues(root)), [], depth: 0);
    }

    /// <summary>
    /// Pass 1. Records each node's value length into <paramref name="sizes"/> in pre-order and
    /// returns the node's total frame size.
    /// </summary>
    private static long Measure(Node node, Tables tables, List<long> sizes, int depth)
    {
        // A backstop, not the primary check: CountValues walks the same tree with the same
        // accounting and runs first, so in practice it reports a too-deep tree before this
        // pass recurses at all. Mutation testing confirms as much — removing this guard alone
        // fails nothing. It stays because it is what keeps *this* recursion bounded, and the
        // guarantee should not depend on the order two private passes happen to run in.
        if (depth > TlvLimits.MaxDepth)
        {
            throw new ArgumentException(
                $"Tree nests deeper than {TlvLimits.MaxDepth}, which no decoder will accept.",
                nameof(node));
        }

        int index = sizes.Count;
        sizes.Add(0);

        long valueLength;
        switch (node)
        {
            case TextNode text:
                if (tables.ValueIds.TryGetValue(text.Value, out int valueId))
                {
                    valueLength = Varint.Size((ulong)valueId);
                }
                else
                {
                    int textBytes = Encoding.UTF8.GetByteCount(text.Value);
                    valueLength = textBytes;

                    // An id is claimed only by a value that will actually be referenced. The
                    // rest are emitted as TEXT_ONCE and register nothing, which keeps ids
                    // dense; the decoder distinguishes the two by type code, so it needs no
                    // knowledge of the rule that produced them.
                    if (tables.ClaimsId(text.Value, textBytes))
                    {
                        tables.ValueIds.Add(text.Value, tables.ValueIds.Count);
                    }
                }

                break;

            case TypedNode typed:
                long typeHead;
                if (tables.TypeNames.TryGetValue(typed.TypeName, out int typeId))
                {
                    typeHead = Varint.Size((ulong)typeId + 1);
                }
                else
                {
                    int typeBytes = Encoding.UTF8.GetByteCount(typed.TypeName);
                    typeHead = Varint.Size(0) + Varint.Size((ulong)typeBytes) + typeBytes;

                    // Claimed before descending, like element names, so ids follow document
                    // order. The table is its own: a type name never shifts a name or value id.
                    tables.TypeNames.Add(typed.TypeName, tables.TypeNames.Count);
                }

                valueLength = typeHead + Measure(typed.Inner, tables, sizes, depth + 1);
                break;

            case PrimitiveNode primitive:
                valueLength = primitive.Payload.Length;
                break;

            case UnknownNode unknown:
                valueLength = unknown.Payload.Length;
                break;

            case ElementNode element:
                long head;
                if (tables.Names.TryGetValue(element.Name, out int nameId))
                {
                    head = Varint.Size((ulong)nameId + 1);
                }
                else
                {
                    int nameBytes = Encoding.UTF8.GetByteCount(element.Name);
                    head = Varint.Size(0) + Varint.Size((ulong)nameBytes) + nameBytes;

                    // Claimed before descending, so ids follow document order, not the order
                    // subtrees happen to finish in.
                    tables.Names.Add(element.Name, tables.Names.Count);
                }

                long children = 0;

                // Indexed rather than foreach: Children is an IReadOnlyList, so foreach goes
                // through IEnumerator and boxes one enumerator per element, per pass.
                for (int child = 0; child < element.Children.Count; child++)
                {
                    children += Measure(element.Children[child], tables, sizes, depth + 1);
                }

                valueLength = head + children;
                break;

            default:
                throw new ArgumentException($"Unsupported node type {node.GetType()}.", nameof(node));
        }

        sizes[index] = valueLength;
        return FrameSize(TypeOf(node), valueLength);
    }

    /// <summary>
    /// Pass 2. Walks in the same order as <see cref="Measure(Node, Tables, List{long})"/>,
    /// so <paramref name="cursor"/> indexes the matching cached size.
    /// </summary>
    private static void Emit(
        Node node,
        IByteSink sink,
        List<long> sizes,
        ref int cursor,
        Tables tables)
    {
        long valueLength = sizes[cursor++];

        switch (node)
        {
            case TextNode text:
                if (tables.ValueIds.TryGetValue(text.Value, out int valueId))
                {
                    sink.Write([TlvType.TextRef]);
                    Varint.Write((ulong)valueLength, sink);
                    Varint.Write((ulong)valueId, sink);
                }
                else
                {
                    // The measuring pass already counted this value's UTF-8 length, so the
                    // literal branch never rescans the string to size it.
                    bool claimsId = tables.ClaimsId(text.Value, (int)valueLength);

                    sink.Write([claimsId ? TlvType.Text : TlvType.TextOnce]);
                    Varint.Write((ulong)valueLength, sink);
                    WriteUtf8(text.Value, (int)valueLength, sink);

                    if (claimsId)
                    {
                        tables.ValueIds.Add(text.Value, tables.ValueIds.Count);
                    }
                }

                break;

            case TypedNode typed:
                sink.Write([TlvType.Typed]);
                Varint.Write((ulong)valueLength, sink);

                if (tables.TypeNames.TryGetValue(typed.TypeName, out int typeId))
                {
                    Varint.Write((ulong)typeId + 1, sink);
                }
                else
                {
                    int typeBytes = Encoding.UTF8.GetByteCount(typed.TypeName);
                    Varint.Write(0, sink);
                    Varint.Write((ulong)typeBytes, sink);
                    WriteUtf8(typed.TypeName, typeBytes, sink);
                    tables.TypeNames.Add(typed.TypeName, tables.TypeNames.Count);
                }

                Emit(typed.Inner, sink, sizes, ref cursor, tables);
                break;

            case PrimitiveNode primitive:
                EmitValue(primitive.Type, primitive.Payload.Span, sink);
                break;

            case UnknownNode unknown:
                // Written back exactly as it arrived. The codec never understood it and does
                // not need to: the shape nibble was enough to measure and copy it.
                EmitValue(unknown.Type, unknown.Payload.Span, sink);
                break;

            case ElementNode element:
                sink.Write([TlvType.Element]);
                Varint.Write((ulong)valueLength, sink);

                if (tables.Names.TryGetValue(element.Name, out int nameId))
                {
                    Varint.Write((ulong)nameId + 1, sink);
                }
                else
                {
                    int nameBytes = Encoding.UTF8.GetByteCount(element.Name);
                    Varint.Write(0, sink);
                    Varint.Write((ulong)nameBytes, sink);
                    WriteUtf8(element.Name, nameBytes, sink);

                    // Registered before descending, matching the measuring pass exactly.
                    tables.Names.Add(element.Name, tables.Names.Count);
                }

                for (int child = 0; child < element.Children.Count; child++)
                {
                    Emit(element.Children[child], sink, sizes, ref cursor, tables);
                }

                break;

            default:
                throw new ArgumentException($"Unsupported node type {node.GetType()}.", nameof(node));
        }
    }

    /// <summary>
    /// The Type byte a node will be written as.
    /// </summary>
    private static byte TypeOf(Node node) => node switch
    {
        ElementNode => TlvType.Element,
        TypedNode => TlvType.Typed,
        PrimitiveNode primitive => primitive.Type,
        UnknownNode unknown => unknown.Type,

        // Text is the one kind whose Type is not fixed by its node: which of TEXT, TEXT_ONCE
        // and TEXT_REF it becomes depends on the interning tables. All three are shape 0, so
        // the frame size is the same whichever it is.
        TextNode => TlvType.Text,
        _ => throw new ArgumentException($"Unsupported node type {node.GetType()}.", nameof(node)),
    };

    /// <summary>
    /// Total frame size, header included, for a payload of <paramref name="valueLength"/> bytes.
    /// </summary>
    /// <remarks>
    /// The shape nibble decides whether a Length is written at all. That is the point of the
    /// split: a fixed-width value pays one byte of Type and nothing else, where the old
    /// uniform Type-Length-Value frame charged a byte to state a width already implied.
    /// </remarks>
    private static long FrameSize(byte type, long valueLength) => TlvType.ShapeOf(type) switch
    {
        PayloadShape.LengthPrefixed => 1 + Varint.Size((ulong)valueLength) + valueLength,

        // Every other shape is self-delimiting, so no Length is written. An extension's
        // subtype and length are part of its payload, which keeps them in wire order and
        // makes re-encoding a copy rather than a reconstruction.
        PayloadShape.Empty or PayloadShape.Varint or PayloadShape.Fixed
            or PayloadShape.Extension => 1 + valueLength,
        _ => throw new ArgumentException($"Type 0x{type:X2} has no writable shape.", nameof(type)),
    };

    /// <summary>
    /// Writes a leaf frame: the Type byte, a Length only where the shape calls for one, then
    /// the payload verbatim.
    /// </summary>
    private static void EmitValue(byte type, ReadOnlySpan<byte> payload, IByteSink sink)
    {
        sink.Write([type]);

        if (TlvType.ShapeOf(type) == PayloadShape.LengthPrefixed)
        {
            Varint.Write((ulong)payload.Length, sink);
        }

        sink.Write(payload);
    }

    /// <summary>
    /// Writes the UTF-8 bytes of <paramref name="text"/> without allocating an array to hold
    /// them.
    /// </summary>
    /// <param name="text">String to encode.</param>
    /// <param name="byteCount">Its UTF-8 length, already known to the caller.</param>
    /// <param name="sink">Destination.</param>
    /// <remarks>
    /// The array-returning <see cref="Encoding.GetBytes(string)"/> allocates once per literal,
    /// which on a document of all-distinct names and values is one garbage array per node.
    /// Short strings go through the stack; anything longer borrows from the array pool.
    /// </remarks>
    private static void WriteUtf8(string text, int byteCount, IByteSink sink)
    {
        if (byteCount <= MaxStackUtf8)
        {
            Span<byte> buffer = stackalloc byte[MaxStackUtf8];
            sink.Write(buffer[..Encoding.UTF8.GetBytes(text, buffer)]);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            sink.Write(rented.AsSpan(0, Encoding.UTF8.GetBytes(text, rented)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Counts how often each distinct text value occurs.
    /// </summary>
    /// <remarks>
    /// Run before the measuring pass, because whether a value's first occurrence claims an id
    /// depends on whether it will be seen again — which is not knowable at the moment the
    /// measuring pass reaches it.
    /// </remarks>
    private static Dictionary<string, int> CountValues(Node root)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        CountValues(root, counts, depth: 0);
        return counts;
    }

    private static void CountValues(Node node, Dictionary<string, int> counts, int depth)
    {
        // This pass recurses too, so it needs the same guard as the measuring pass; without
        // it a too-deep tree would exhaust the stack here, before anything could reject it.
        if (depth > TlvLimits.MaxDepth)
        {
            throw new ArgumentException(
                $"Tree nests deeper than {TlvLimits.MaxDepth}, which no decoder will accept.",
                nameof(node));
        }

        switch (node)
        {
            case TextNode text:
                // One probe, not a read followed by a write. These keys are hashed once per
                // pass and the strings can be long — on a document of 200-character values
                // the second hash was measurable.
                ref int occurrences = ref CollectionsMarshal.GetValueRefOrAddDefault(
                    counts, text.Value, out _);
                occurrences++;
                break;

            case PrimitiveNode:
            case UnknownNode:
                // No text, so nothing to count. Primitives are not interned: a reference
                // costs three bytes and a small integer costs two, so it could only lose.
                break;

            case TypedNode typed:
                CountValues(typed.Inner, counts, depth + 1);
                break;

            case ElementNode element:
                for (int child = 0; child < element.Children.Count; child++)
                {
                    CountValues(element.Children[child], counts, depth + 1);
                }

                break;

            default:
                throw new ArgumentException($"Unsupported node type {node.GetType()}.", nameof(node));
        }
    }

    /// <summary>
    /// The interning state both passes build independently, from empty.
    /// </summary>
    /// <param name="occurrences">
    /// How often each value appears, from <see cref="CountValues(Node)"/>. Shared between the
    /// passes because it is derived from the tree alone and cannot drift.
    /// </param>
    private sealed class Tables(Dictionary<string, int> occurrences)
    {
        internal Dictionary<string, int> Names { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Type names, in their own id space so they shift nothing else.
        /// </summary>
        internal Dictionary<string, int> TypeNames { get; } = new(StringComparer.Ordinal);

        /// <summary>Values that claimed an id, in first-occurrence document order.</summary>
        internal Dictionary<string, int> ValueIds { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Whether a value should claim an id, and so be emitted as <c>TEXT</c> rather than
        /// <c>TEXT_ONCE</c>.
        /// </summary>
        /// <remarks>
        /// Over k occurrences of an L-byte value, referencing saves (k-1)(L-1). That is zero
        /// whenever k is 1 or L is 1, and an id claimed for no gain still consumes id space,
        /// pushing later references from one varint byte into two. Both conditions are
        /// therefore required.
        /// </remarks>
        internal bool ClaimsId(string value, int byteCount) =>
            byteCount >= MinInternedValueLength && occurrences[value] > 1;
    }
}
