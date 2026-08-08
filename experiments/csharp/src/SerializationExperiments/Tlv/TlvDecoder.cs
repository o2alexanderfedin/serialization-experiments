using System.Text;

namespace SerializationExperiments.Tlv;

/// <summary>
/// Decodes a TLV document back into a <see cref="Node"/> tree.
/// </summary>
/// <remarks>
/// Names and text values are rebuilt from the byte stream alone: a literal defines the next
/// id, and ids are consumed in the order literals appear. Nothing is shared with the encoder
/// beyond the bytes, which is what lets a document carry tag names the decoder has never
/// seen. Every text literal is registered whether or not the encoder ever references it, so
/// the decoder needs no knowledge of the encoder's threshold for what is worth interning.
/// </remarks>
public static class TlvDecoder
{
    /// <summary>Guards against stack exhaustion from a maliciously deep document.</summary>
    private const int MaxDepth = 512;

    /// <summary>Decodes a complete document.</summary>
    /// <param name="data">Encoded document; must be consumed exactly.</param>
    /// <returns>The decoded tree.</returns>
    /// <exception cref="TlvFormatException">The bytes are malformed, truncated, or have trailing content.</exception>
    public static Node Decode(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var tables = new Tables();
        Node root = DecodeNode(data, ref offset, tables, depth: 0);

        if (offset != data.Length)
        {
            throw new TlvFormatException(
                $"{data.Length - offset} trailing byte(s) after the root frame at offset {offset}.");
        }

        return root;
    }

    private static Node DecodeNode(ReadOnlySpan<byte> data, ref int offset, Tables tables, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new TlvFormatException($"Nesting deeper than {MaxDepth} at offset {offset}.");
        }

        if (offset >= data.Length)
        {
            throw new TlvFormatException($"Expected a frame at offset {offset}, but the buffer ended.");
        }

        byte type = data[offset++];
        ulong length = Varint.Read(data, ref offset);

        if (length > (ulong)(data.Length - offset))
        {
            throw new TlvFormatException(
                $"Frame at offset {offset} declares {length} bytes but only {data.Length - offset} remain.");
        }

        int end = offset + (int)length;

        switch (type)
        {
            case TlvType.Text:
                string value = Encoding.UTF8.GetString(data[offset..end]);
                offset = end;

                // Every literal is registered, so ids stay in step with the encoder. Whether
                // a repeat was worth referencing is the encoder's decision alone.
                tables.Values.Add(value);
                return new TextNode(value);

            case TlvType.TextRef:
                ulong valueId = Varint.Read(data, ref offset);
                if (valueId >= (ulong)tables.Values.Count)
                {
                    throw new TlvFormatException(
                        $"Value reference {valueId} at offset {offset} is not defined; {tables.Values.Count} value(s) known.");
                }

                if (offset != end)
                {
                    throw new TlvFormatException(
                        $"Value reference frame ending at {offset} does not fill its length, which ends at {end}.");
                }

                // The existing instance: a reference costs no UTF-8 decode and no allocation.
                return new TextNode(tables.Values[(int)valueId]);

            case TlvType.Element:
                return DecodeElement(data, ref offset, end, tables, depth);

            default:
                throw new TlvFormatException(
                    $"Unknown type 0x{type:X2} at offset {offset - 1 - Varint.Size(length)}.");
        }
    }

    private static ElementNode DecodeElement(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end,
        Tables tables,
        int depth)
    {
        ulong nameRef = Varint.Read(data, ref offset);
        string name;

        if (nameRef == 0)
        {
            ulong nameLength = Varint.Read(data, ref offset);
            if (nameLength > (ulong)(end - offset))
            {
                throw new TlvFormatException(
                    $"Name at offset {offset} declares {nameLength} bytes, past the end of its element.");
            }

            name = Encoding.UTF8.GetString(data.Slice(offset, (int)nameLength));
            offset += (int)nameLength;

            // Registered before children are read, mirroring the encoder's pre-order assignment.
            tables.Names.Add(name);
        }
        else
        {
            ulong id = nameRef - 1;
            if (id >= (ulong)tables.Names.Count)
            {
                throw new TlvFormatException(
                    $"Name reference {id} at offset {offset} is not defined; {tables.Names.Count} name(s) known.");
            }

            name = tables.Names[(int)id];
        }

        List<Node> children = [];
        while (offset < end)
        {
            children.Add(DecodeNode(data, ref offset, tables, depth + 1));
        }

        if (offset != end)
        {
            throw new TlvFormatException($"Child frames overran their element, ending at {offset} instead of {end}.");
        }

        return new ElementNode(name, children);
    }

    /// <summary>
    /// Interning state rebuilt from the byte stream alone; nothing is shared with the encoder.
    /// </summary>
    private sealed class Tables
    {
        internal List<string> Names { get; } = [];

        internal List<string> Values { get; } = [];
    }
}
