using System.Text;

namespace SerializationExperiments.Tlv;

/// <summary>
/// Decodes a TLV document back into a <see cref="Node"/> tree.
/// </summary>
/// <remarks>
/// Names are rebuilt from the byte stream alone: a literal defines the next id, and ids are
/// consumed in the order literals appear. Nothing is shared with the encoder beyond the
/// bytes, which is what lets a document carry tag names the decoder has never seen.
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
        List<string> names = [];
        Node root = DecodeNode(data, ref offset, names, depth: 0);

        if (offset != data.Length)
        {
            throw new TlvFormatException(
                $"{data.Length - offset} trailing byte(s) after the root frame at offset {offset}.");
        }

        return root;
    }

    private static Node DecodeNode(ReadOnlySpan<byte> data, ref int offset, List<string> names, int depth)
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
                return new TextNode(value);

            case TlvType.Element:
                return DecodeElement(data, ref offset, end, names, depth);

            default:
                throw new TlvFormatException(
                    $"Unknown type 0x{type:X2} at offset {offset - 1 - Varint.Size(length)}.");
        }
    }

    private static ElementNode DecodeElement(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end,
        List<string> names,
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
            names.Add(name);
        }
        else
        {
            ulong id = nameRef - 1;
            if (id >= (ulong)names.Count)
            {
                throw new TlvFormatException(
                    $"Name reference {id} at offset {offset} is not defined; {names.Count} name(s) known.");
            }

            name = names[(int)id];
        }

        List<Node> children = [];
        while (offset < end)
        {
            children.Add(DecodeNode(data, ref offset, names, depth + 1));
        }

        if (offset != end)
        {
            throw new TlvFormatException($"Child frames overran their element, ending at {offset} instead of {end}.");
        }

        return new ElementNode(name, children);
    }
}
