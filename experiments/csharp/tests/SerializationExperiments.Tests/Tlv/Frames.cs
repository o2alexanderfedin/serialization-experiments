using SerializationExperiments.Tlv;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Walks encoded frames so tests can assert on structure rather than on raw bytes.
/// </summary>
/// <remarks>
/// Scanning a document for a type-code byte does not work: <c>0x04</c> is equally the
/// <c>TEXT_ONCE</c> code and the length of a four-character element name. Tests that need to
/// know which literals claimed an id have to parse, so this parses — minimally, and
/// deliberately without reusing the decoder, so a decoder bug cannot hide behind it.
/// </remarks>
internal static class Frames
{
    /// <summary>A decoded frame header.</summary>
    /// <param name="Type">The Type byte.</param>
    /// <param name="ValueId">Referenced value id, for <c>TEXT_REF</c> frames only.</param>
    /// <param name="IdWidth">Bytes the referenced id occupied, for <c>TEXT_REF</c> only.</param>
    internal readonly record struct Frame(byte Type, ulong ValueId, int IdWidth);

    /// <summary>Every frame in <paramref name="data"/>, in document order.</summary>
    internal static List<Frame> Walk(ReadOnlySpan<byte> data)
    {
        List<Frame> frames = [];
        int offset = 0;
        Walk(data, ref offset, frames);
        return frames;
    }

    /// <summary>Type codes of every frame, in document order.</summary>
    internal static List<byte> TypeCodes(ReadOnlySpan<byte> data) =>
        Walk(data).Select(frame => frame.Type).ToList();

    private static void Walk(ReadOnlySpan<byte> data, ref int offset, List<Frame> frames)
    {
        byte type = data[offset++];

        if (TlvType.ShapeOf(type) != PayloadShape.LengthPrefixed)
        {
            frames.Add(new Frame(type, 0, 0));
            SkipSelfDelimiting(data, ref offset, type);
            return;
        }

        ulong length = ReadVarint(data, ref offset);
        int end = offset + (int)length;

        switch (type)
        {
            case TlvType.Text:
            case TlvType.TextOnce:
                frames.Add(new Frame(type, 0, 0));
                offset = end;
                break;

            case TlvType.TextRef:
                int idStart = offset;
                ulong id = ReadVarint(data, ref offset);
                frames.Add(new Frame(type, id, offset - idStart));
                break;

            case TlvType.Element:
            case TlvType.Typed:
                frames.Add(new Frame(type, 0, 0));
                ulong nameRef = ReadVarint(data, ref offset);
                if (nameRef == 0)
                {
                    ulong nameLength = ReadVarint(data, ref offset);
                    offset += (int)nameLength;
                }

                while (offset < end)
                {
                    Walk(data, ref offset, frames);
                }

                break;

            default:
                throw new InvalidOperationException($"Unknown type 0x{type:X2} at offset {offset}.");
        }
    }

    private static void SkipSelfDelimiting(ReadOnlySpan<byte> data, ref int offset, byte type)
    {
        switch (TlvType.ShapeOf(type))
        {
            case PayloadShape.Empty:
                break;
            case PayloadShape.Varint:
                ReadVarint(data, ref offset);
                break;
            case PayloadShape.Fixed:
                offset += TlvType.FixedWidthOf(type);
                break;
            case PayloadShape.Extension:
                ReadVarint(data, ref offset);
                offset += (int)ReadVarint(data, ref offset);
                break;
            default:
                throw new InvalidOperationException($"Type 0x{type:X2} has no skippable shape.");
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }
}
