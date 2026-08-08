using System.Buffers;
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
    /// would be replaced by a 3-byte reference. The threshold lives only here — the decoder
    /// registers every literal it sees, so it needs no knowledge of the rule.
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
        long total = Measure(root, new Tables(), sizes, depth: 0);

        if (total > Array.MaxLength)
        {
            throw new InvalidOperationException(
                $"Document measures {total} bytes, past the {Array.MaxLength}-byte array limit. " +
                $"Encode into a stream sink instead.");
        }

        byte[] result = new byte[total];
        EmitMeasured(root, new BufferSink(result), sizes, total);
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
        long expected = Measure(root, new Tables(), sizes, depth: 0);
        return EmitMeasured(root, sink, sizes, expected);
    }

    /// <summary>
    /// Runs the emit pass and checks it against what the measuring pass counted.
    /// </summary>
    private static long EmitMeasured(Node root, IByteSink sink, List<long> sizes, long expected)
    {
        long before = sink.BytesWritten;
        int cursor = 0;

        // Fresh tables: the emit pass must rediscover names and values in the same order the
        // measuring pass did, or every length already counted would be wrong.
        Emit(root, sink, sizes, ref cursor, new Tables());

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
        return Measure(root, new Tables(), [], depth: 0);
    }

    /// <summary>
    /// Pass 1. Records each node's value length into <paramref name="sizes"/> in pre-order and
    /// returns the node's total frame size.
    /// </summary>
    private static long Measure(Node node, Tables tables, List<long> sizes, int depth)
    {
        // Checked here rather than in the emit pass so a too-deep tree is rejected before any
        // byte reaches the sink, and so this pass cannot itself overflow the stack.
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
                if (tables.Values.TryGetValue(text.Value, out InternedValue seen) && seen.WorthReferencing)
                {
                    valueLength = Varint.Size((ulong)seen.Id);
                }
                else
                {
                    int textBytes = Encoding.UTF8.GetByteCount(text.Value);
                    valueLength = textBytes;

                    // Registered whether or not it is worth referencing, so ids stay in step
                    // with the decoder, which registers every literal it reads.
                    tables.Values.TryAdd(text.Value, new InternedValue(tables.Values.Count, textBytes));
                }

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
                foreach (Node child in element.Children)
                {
                    children += Measure(child, tables, sizes, depth + 1);
                }

                valueLength = head + children;
                break;

            default:
                throw new ArgumentException($"Unsupported node type {node.GetType()}.", nameof(node));
        }

        sizes[index] = valueLength;
        return 1 + Varint.Size((ulong)valueLength) + valueLength;
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
                if (tables.Values.TryGetValue(text.Value, out InternedValue seen) && seen.WorthReferencing)
                {
                    sink.Write([TlvType.TextRef]);
                    Varint.Write((ulong)valueLength, sink);
                    Varint.Write((ulong)seen.Id, sink);
                }
                else
                {
                    // The measuring pass already counted this value's UTF-8 length, so the
                    // literal branch never rescans the string to size it.
                    sink.Write([TlvType.Text]);
                    Varint.Write((ulong)valueLength, sink);
                    WriteUtf8(text.Value, (int)valueLength, sink);
                    tables.Values.TryAdd(text.Value, new InternedValue(tables.Values.Count, (int)valueLength));
                }

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

                foreach (Node child in element.Children)
                {
                    Emit(child, sink, sizes, ref cursor, tables);
                }

                break;

            default:
                throw new ArgumentException($"Unsupported node type {node.GetType()}.", nameof(node));
        }
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
    /// The interning state both passes build independently, from empty.
    /// </summary>
    private sealed class Tables
    {
        internal Dictionary<string, int> Names { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, InternedValue> Values { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A registered text value: its id, and whether referencing it actually saves bytes.
    /// </summary>
    /// <param name="Id">Value id, assigned in first-occurrence document order.</param>
    /// <param name="ByteCount">UTF-8 length, cached so neither pass recomputes it.</param>
    private readonly record struct InternedValue(int Id, int ByteCount)
    {
        internal bool WorthReferencing => this.ByteCount >= MinInternedValueLength;
    }
}
