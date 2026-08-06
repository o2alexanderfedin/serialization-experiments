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
/// Both passes assign name ids from an empty table, in first-occurrence document order.
/// The measuring pass therefore is not pure arithmetic — whether an element costs a literal
/// or a one-byte reference depends on the table — and the emit pass must start from a fresh
/// table or it would write references where the measuring pass counted literals.
/// </para>
/// </remarks>
public static class TlvEncoder
{
    /// <summary>Encodes <paramref name="root"/> to a new array.</summary>
    /// <param name="root">Tree to encode.</param>
    /// <returns>The encoded document.</returns>
    public static byte[] Encode(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using var buffer = new MemoryStream();
        Encode(root, new StreamSink(buffer));
        return buffer.ToArray();
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
        long expected = Measure(root, NewNameTable(), sizes);

        long before = sink.BytesWritten;
        int cursor = 0;

        // A fresh table: the emit pass must rediscover names in the same order the
        // measuring pass did, or every length already counted would be wrong.
        Emit(root, sink, sizes, ref cursor, NewNameTable());

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
    public static long Measure(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Measure(root, NewNameTable(), []);
    }

    private static Dictionary<string, int> NewNameTable() => new(StringComparer.Ordinal);

    /// <summary>
    /// Pass 1. Records each node's value length into <paramref name="sizes"/> in pre-order and
    /// returns the node's total frame size.
    /// </summary>
    private static long Measure(Node node, Dictionary<string, int> names, List<long> sizes)
    {
        int index = sizes.Count;
        sizes.Add(0);

        long valueLength;
        switch (node)
        {
            case TextNode text:
                valueLength = Encoding.UTF8.GetByteCount(text.Value);
                break;

            case ElementNode element:
                long head;
                if (names.TryGetValue(element.Name, out int id))
                {
                    head = Varint.Size((ulong)id + 1);
                }
                else
                {
                    int nameBytes = Encoding.UTF8.GetByteCount(element.Name);
                    head = Varint.Size(0) + Varint.Size((ulong)nameBytes) + nameBytes;

                    // Claimed before descending, so ids follow document order, not the order
                    // subtrees happen to finish in.
                    names.Add(element.Name, names.Count);
                }

                long children = 0;
                foreach (Node child in element.Children)
                {
                    children += Measure(child, names, sizes);
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
    /// Pass 2. Walks in the same order as <see cref="Measure(Node, Dictionary{string, int}, List{long})"/>,
    /// so <paramref name="cursor"/> indexes the matching cached size.
    /// </summary>
    private static void Emit(
        Node node,
        IByteSink sink,
        List<long> sizes,
        ref int cursor,
        Dictionary<string, int> names)
    {
        long valueLength = sizes[cursor++];

        switch (node)
        {
            case TextNode text:
                sink.Write([TlvType.Text]);
                Varint.Write((ulong)valueLength, sink);
                sink.Write(Encoding.UTF8.GetBytes(text.Value));
                break;

            case ElementNode element:
                sink.Write([TlvType.Element]);
                Varint.Write((ulong)valueLength, sink);

                if (names.TryGetValue(element.Name, out int id))
                {
                    Varint.Write((ulong)id + 1, sink);
                }
                else
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(element.Name);
                    Varint.Write(0, sink);
                    Varint.Write((ulong)nameBytes.Length, sink);
                    sink.Write(nameBytes);

                    // Registered before descending, matching the measuring pass exactly.
                    names.Add(element.Name, names.Count);
                }

                foreach (Node child in element.Children)
                {
                    Emit(child, sink, sizes, ref cursor, names);
                }

                break;

            default:
                throw new ArgumentException($"Unsupported node type {node.GetType()}.", nameof(node));
        }
    }
}
