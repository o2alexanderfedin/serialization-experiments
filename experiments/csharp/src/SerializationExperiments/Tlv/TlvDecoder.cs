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
    /// <summary>Decodes a complete document.</summary>
    /// <param name="data">Encoded document; must be consumed exactly.</param>
    /// <param name="options">
    /// Decoder behaviour the format does not dictate. Defaults to
    /// <see cref="TlvDecoderOptions.Default"/>.
    /// </param>
    /// <returns>The decoded tree.</returns>
    /// <exception cref="TlvFormatException">The bytes are malformed, truncated, or have trailing content.</exception>
    public static Node Decode(ReadOnlySpan<byte> data, TlvDecoderOptions? options = null)
    {
        int offset = 0;
        var tables = new Tables(options ?? TlvDecoderOptions.Default);
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
        if (depth > TlvLimits.MaxDepth)
        {
            throw new TlvFormatException($"Nesting deeper than {TlvLimits.MaxDepth} at offset {offset}.");
        }

        if (offset >= data.Length)
        {
            throw new TlvFormatException($"Expected a frame at offset {offset}, but the buffer ended.");
        }

        byte type = data[offset++];

        // The shape nibble decides how far this frame reaches. Only length-prefixed frames
        // carry a Length; everything else is self-delimiting, which is the whole reason a
        // fixed-width value costs one byte of header rather than two.
        PayloadShape shape = TlvType.ShapeOf(type);
        if (shape != PayloadShape.LengthPrefixed)
        {
            return DecodeSelfDelimiting(data, ref offset, type, shape, tables);
        }

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

                // The type code says this literal claims the next id. Why the encoder
                // decided that — how often the value recurs, how long it is — is the
                // encoder's business, and can change without touching this side.
                var textNode = new TextNode(value);
                tables.Values.Add(textNode);
                return textNode;

            case TlvType.TextOnce:
                string once = Encoding.UTF8.GetString(data[offset..end]);
                offset = end;

                // Identical to TEXT except that it registers nothing, which is what keeps
                // the id space dense enough for references to stay one byte.
                return new TextNode(once);

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

                // Sharing the instance costs no UTF-8 decode and no allocation, and is
                // unobservable for immutable values. Callers whose object model attaches
                // meaning to reference identity can opt out.
                Node referenced = tables.Values[(int)valueId];
                if (tables.Options.ShareValueInstances)
                {
                    return referenced;
                }

                return referenced switch
                {
                    TextNode text => new TextNode(new string(text.Value.AsSpan())),
                    PrimitiveNode primitive => new PrimitiveNode(
                        primitive.Type, primitive.Payload.ToArray()),
                    _ => referenced,
                };

            case TlvType.Intern:
                return DecodeIntern(data, ref offset, end, tables, depth);

            case TlvType.Element:
                return DecodeElement(data, ref offset, end, tables, depth);

            case TlvType.Typed:
                return DecodeTyped(data, ref offset, end, tables, depth);

            case TlvType.Bytes:
                return Leaf(type, data[offset..end], ref offset, end, tables);

            case TlvType.Reserved:
                throw new TlvFormatException(
                    $"Type 0x00 at offset {offset - 1 - Varint.Size(length)} is reserved and never valid.");

            default:
                // A length-prefixed frame whose type this reader does not know. The length
                // says exactly how far it reaches, so it is carried through intact rather
                // than rejected — refusing what you do not recognise is what ossifies a
                // format, and the bytes are never interpreted.
                return Leaf(type, data[offset..end], ref offset, end, tables);
        }
    }

    /// <summary>
    /// Reads a frame whose width comes from its shape rather than a Length field.
    /// </summary>
    private static Node DecodeSelfDelimiting(
        ReadOnlySpan<byte> data,
        ref int offset,
        byte type,
        PayloadShape shape,
        Tables tables)
    {
        int start = offset;

        switch (shape)
        {
            case PayloadShape.Empty:
                break;

            case PayloadShape.Varint:
                Varint.Read(data, ref offset);
                break;

            case PayloadShape.Fixed:
                int width = TlvType.FixedWidthOf(type);
                if (width > data.Length - offset)
                {
                    throw new TlvFormatException(
                        $"Frame type 0x{type:X2} at offset {offset - 1} needs {width} bytes " +
                        $"but only {data.Length - offset} remain.");
                }

                offset += width;
                break;

            case PayloadShape.Extension:
                Varint.Read(data, ref offset);
                ulong extensionLength = Varint.Read(data, ref offset);
                if (extensionLength > (ulong)(data.Length - offset))
                {
                    throw new TlvFormatException(
                        $"Extension at offset {offset} declares {extensionLength} bytes " +
                        $"but only {data.Length - offset} remain.");
                }

                offset += (int)extensionLength;
                break;

            default:
                // 0xB_ to 0xE_ carry no width. Nothing can be allocated there precisely
                // because a reader cannot step over it, so this is the one unknown that has
                // to be an error rather than something to carry along.
                throw new TlvFormatException(
                    $"Frame type 0x{type:X2} at offset {offset - 1} has a reserved shape and cannot be skipped.");
        }

        return Leaf(type, data[start..offset], ref offset, offset, tables);
    }

    /// <summary>
    /// Builds a leaf node from a Type byte and its payload, understood or not.
    /// </summary>
    private static Node Leaf(
        byte type,
        ReadOnlySpan<byte> payload,
        ref int offset,
        int end,
        Tables tables)
    {
        offset = end;

        if (!TlvType.IsKnown(type))
        {
            if (!tables.Options.AllowUnknownTypes)
            {
                throw new TlvFormatException(
                    $"Frame type 0x{type:X2} is not known to this decoder, and unknown types are not allowed.");
            }

            return new UnknownNode(type, payload.ToArray());
        }

        if (type is TlvType.Float32 or TlvType.Float64)
        {
            RejectNonCanonicalNaN(type, payload);
        }

        return new PrimitiveNode(type, payload.ToArray());
    }

    /// <summary>
    /// Rejects every NaN bit pattern but the canonical quiet one.
    /// </summary>
    /// <remarks>
    /// Without this a document has as many encodings as there are NaN payloads, which breaks
    /// the one-document-one-encoding rule that byte-exact re-encoding and any hashing or
    /// signing of these bytes depend on.
    /// </remarks>
    private static void RejectNonCanonicalNaN(byte type, ReadOnlySpan<byte> payload)
    {
        if (type == TlvType.Float32)
        {
            uint bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (float.IsNaN(BitConverter.UInt32BitsToSingle(bits)) && bits != 0x7FC00000)
            {
                throw new TlvFormatException($"Non-canonical binary32 NaN 0x{bits:X8}.");
            }

            return;
        }

        ulong wide = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload);
        if (double.IsNaN(BitConverter.UInt64BitsToDouble(wide)) && wide != 0x7FF8000000000000)
        {
            throw new TlvFormatException($"Non-canonical binary64 NaN 0x{wide:X16}.");
        }
    }

    /// <summary>
    /// Reads a type-tagged frame into a <see cref="TypedNode"/>.
    /// </summary>
    /// <remarks>
    /// The type name is returned as text and nothing else happens to it. No lookup, no
    /// assembly load, no construction: a name the caller does not recognise costs them a
    /// branch, not a gadget chain. Because the frame is length-prefixed, an unrecognised name
    /// can also simply be re-encoded unchanged, which is what keeps adding a derived type
    /// from breaking existing readers.
    /// </remarks>
    private static TypedNode DecodeTyped(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end,
        Tables tables,
        int depth)
    {
        if (!tables.Options.AllowTypeNames)
        {
            throw new TlvFormatException(
                $"Type-tagged frame at offset {offset} rejected: type names are not allowed by this decoder.");
        }

        ulong typeRef = Varint.Read(data, ref offset);
        string typeName;

        if (typeRef == 0)
        {
            ulong nameLength = Varint.Read(data, ref offset);
            if (nameLength > (ulong)(end - offset))
            {
                throw new TlvFormatException(
                    $"Type name at offset {offset} declares {nameLength} bytes, past the end of its frame.");
            }

            typeName = Encoding.UTF8.GetString(data.Slice(offset, (int)nameLength));
            offset += (int)nameLength;

            // Registered before descending, mirroring the encoder's pre-order assignment.
            tables.TypeNames.Add(typeName);
        }
        else
        {
            ulong id = typeRef - 1;
            if (id >= (ulong)tables.TypeNames.Count)
            {
                throw new TlvFormatException(
                    $"Type reference {id} at offset {offset} is not defined; {tables.TypeNames.Count} type name(s) known.");
            }

            typeName = tables.TypeNames[(int)id];
        }

        Node inner = DecodeNode(data, ref offset, tables, depth + 1);

        if (offset != end)
        {
            throw new TlvFormatException(
                $"Type-tagged frame ending at {offset} does not fill its length, which ends at {end}.");
        }

        return new TypedNode(typeName, inner);
    }

    /// <summary>
    /// Reads an <c>INTERN</c> frame: one value frame that claims the next value id.
    /// </summary>
    /// <remarks>
    /// The wrapper carries no payload of its own — it decodes to the value it wraps, exactly
    /// as a <c>TEXT</c> frame decodes to its text. Re-encoding stays byte-exact because the
    /// encoder rederives which values are worth interning from occurrence counts, so it wraps
    /// the same first occurrence again.
    /// </remarks>
    private static Node DecodeIntern(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end,
        Tables tables,
        int depth)
    {
        Node inner = DecodeNode(data, ref offset, tables, depth + 1);

        if (offset != end)
        {
            throw new TlvFormatException(
                $"Intern frame ending at {offset} does not fill its length, which ends at {end}.");
        }

        // Only a primitive may claim an id this way. Text has TEXT for the purpose and would
        // register twice; a constructed frame has no single value to register; and an unknown
        // frame must go back exactly as it arrived, which rewriting it as a reference is not.
        // Admitting any of them would put the decoder's id list out of step with the
        // encoder's, and this format has already been bitten once by references that resolve
        // to the wrong value while every length still checks out.
        if (inner is not PrimitiveNode)
        {
            throw new TlvFormatException(
                $"Intern frame at offset {end} wraps a {inner.GetType().Name}; only a primitive may claim a value id.");
        }

        tables.Values.Add(inner);
        return inner;
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
    private sealed class Tables(TlvDecoderOptions options)
    {
        internal TlvDecoderOptions Options { get; } = options;

        internal List<string> Names { get; } = [];

        internal List<string> TypeNames { get; } = [];

        /// <summary>
        /// Values that claimed an id, text and primitives alike, in the order their claiming
        /// frames were read.
        /// </summary>
        /// <remarks>
        /// One list, not two, because the encoder numbers from one counter. A separate list
        /// per kind would agree with the encoder right up until a document mixed them.
        /// </remarks>
        internal List<Node> Values { get; } = [];
    }
}
