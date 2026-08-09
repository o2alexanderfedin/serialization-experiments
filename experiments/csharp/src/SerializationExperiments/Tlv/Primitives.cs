using System.Buffers.Binary;

namespace SerializationExperiments.Tlv;

/// <summary>
/// What kind of value a <see cref="PrimitiveNode"/> holds, independent of its encoding.
/// </summary>
public enum PrimitiveKind
{
    /// <summary>The absence of a value.</summary>
    Null,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A signed integer of any width.</summary>
    SignedInteger,

    /// <summary>An unsigned integer of any width.</summary>
    UnsignedInteger,

    /// <summary>An IEEE 754 binary32 value.</summary>
    Single,

    /// <summary>An IEEE 754 binary64 value.</summary>
    Double,

    /// <summary>A UUID.</summary>
    Guid,

    /// <summary>A blob of octets.</summary>
    Bytes,
}

/// <summary>
/// Builds and reads <see cref="PrimitiveNode"/> values.
/// </summary>
/// <remarks>
/// The node itself holds a Type byte and raw bytes; this is where those become numbers.
/// Keeping the conversion out of the node is what lets the encoder and decoder treat every
/// primitive identically — write the type, write the payload — regardless of how many types
/// exist.
/// </remarks>
public static class Primitives
{
    /// <summary>What kind of value a node holds.</summary>
    /// <param name="node">Node to classify.</param>
    /// <returns>Its kind.</returns>
    /// <remarks>
    /// Callers switch on this rather than on the node's Type byte. The byte is a wire
    /// detail — which spelling of an integer was used, whether a boolean rode as its own
    /// type code — and a caller reading a value should not have to know the encoding to
    /// find out it is looking at a number.
    /// </remarks>
    public static PrimitiveKind KindOf(this PrimitiveNode node) => Require(node).Type switch
    {
        TlvType.Null => PrimitiveKind.Null,
        TlvType.True or TlvType.False => PrimitiveKind.Boolean,
        TlvType.SInt => PrimitiveKind.SignedInteger,
        TlvType.UInt => PrimitiveKind.UnsignedInteger,
        TlvType.Float32 => PrimitiveKind.Single,
        TlvType.Float64 => PrimitiveKind.Double,
        TlvType.Guid => PrimitiveKind.Guid,
        TlvType.Bytes => PrimitiveKind.Bytes,
        _ => throw new InvalidOperationException(
            $"Frame type 0x{node.Type:X2} is not a primitive this build understands."),
    };

    /// <summary>The canonical quiet NaN for binary64.</summary>
    private const ulong CanonicalNaN64 = 0x7FF8000000000000;

    /// <summary>The canonical quiet NaN for binary32.</summary>
    private const uint CanonicalNaN32 = 0x7FC00000;

    /// <summary>The absence of a value.</summary>
    /// <returns>A null node.</returns>
    public static PrimitiveNode Null() => new(TlvType.Null, ReadOnlyMemory<byte>.Empty);

    /// <summary>A boolean.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A boolean node, whose Type byte is the whole frame.</returns>
    public static PrimitiveNode Bool(bool value) =>
        new(value ? TlvType.True : TlvType.False, ReadOnlyMemory<byte>.Empty);

    /// <summary>A signed integer of any width.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A node holding the ZigZag varint of <paramref name="value"/>.</returns>
    public static PrimitiveNode Int(long value) =>
        new(TlvType.SInt, Varint.ToBytes(ZigZag.Encode(value)));

    /// <summary>An unsigned integer of any width.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A node holding the varint of <paramref name="value"/>.</returns>
    public static PrimitiveNode UInt(ulong value) => new(TlvType.UInt, Varint.ToBytes(value));

    /// <summary>An IEEE 754 binary32 value.</summary>
    /// <param name="value">The value. NaN is canonicalised.</param>
    /// <returns>A four-byte node.</returns>
    public static PrimitiveNode Float(float value)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload,
            float.IsNaN(value) ? CanonicalNaN32 : BitConverter.SingleToUInt32Bits(value));
        return new PrimitiveNode(TlvType.Float32, payload);
    }

    /// <summary>An IEEE 754 binary64 value.</summary>
    /// <param name="value">The value. NaN is canonicalised.</param>
    /// <returns>An eight-byte node.</returns>
    public static PrimitiveNode Double(double value)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload,
            double.IsNaN(value) ? CanonicalNaN64 : BitConverter.DoubleToUInt64Bits(value));
        return new PrimitiveNode(TlvType.Float64, payload);
    }

    /// <summary>A UUID.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A sixteen-byte node.</returns>
    public static PrimitiveNode Guid(Guid value) => new(TlvType.Guid, value.ToByteArray());

    /// <summary>A blob of octets.</summary>
    /// <param name="value">The bytes; copied.</param>
    /// <returns>A length-prefixed node.</returns>
    public static PrimitiveNode Bytes(ReadOnlySpan<byte> value) =>
        new(TlvType.Bytes, value.ToArray());

    /// <summary>Whether <paramref name="node"/> is the null value.</summary>
    /// <param name="node">Node to test.</param>
    /// <returns><see langword="true"/> for a null node.</returns>
    public static bool IsNull(this PrimitiveNode node) => Require(node).Type == TlvType.Null;

    /// <summary>Reads a boolean.</summary>
    /// <param name="node">Node to read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The node is not a boolean.</exception>
    public static bool AsBool(this PrimitiveNode node) => Require(node).Type switch
    {
        TlvType.True => true,
        TlvType.False => false,
        _ => throw Mismatch(node, "a boolean"),
    };

    /// <summary>Reads a signed integer.</summary>
    /// <param name="node">Node to read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The node is not a signed integer.</exception>
    public static long AsInt(this PrimitiveNode node) => Require(node).Type == TlvType.SInt
        ? ZigZag.Decode(ReadVarint(node))
        : throw Mismatch(node, "a signed integer");

    /// <summary>Reads an unsigned integer.</summary>
    /// <param name="node">Node to read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The node is not an unsigned integer.</exception>
    public static ulong AsUInt(this PrimitiveNode node) => Require(node).Type == TlvType.UInt
        ? ReadVarint(node)
        : throw Mismatch(node, "an unsigned integer");

    /// <summary>Reads a binary32 value.</summary>
    /// <param name="node">Node to read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The node is not a binary32 value.</exception>
    public static float AsFloat(this PrimitiveNode node) => Require(node).Type == TlvType.Float32
        ? BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(node.Payload.Span))
        : throw Mismatch(node, "a binary32 value");

    /// <summary>Reads a binary64 value.</summary>
    /// <param name="node">Node to read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The node is not a binary64 value.</exception>
    public static double AsDouble(this PrimitiveNode node) => Require(node).Type == TlvType.Float64
        ? BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(node.Payload.Span))
        : throw Mismatch(node, "a binary64 value");

    /// <summary>Reads a UUID.</summary>
    /// <param name="node">Node to read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The node is not a UUID.</exception>
    public static Guid AsGuid(this PrimitiveNode node) => Require(node).Type == TlvType.Guid
        ? new Guid(node.Payload.Span)
        : throw Mismatch(node, "a UUID");

    /// <summary>Reads a blob.</summary>
    /// <param name="node">Node to read.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidOperationException">The node is not a blob.</exception>
    public static ReadOnlySpan<byte> AsBytes(this PrimitiveNode node) =>
        Require(node).Type == TlvType.Bytes
            ? node.Payload.Span
            : throw Mismatch(node, "a blob");

    private static ulong ReadVarint(PrimitiveNode node)
    {
        int offset = 0;
        return Varint.Read(node.Payload.Span, ref offset);
    }

    private static PrimitiveNode Require(PrimitiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node;
    }

    private static InvalidOperationException Mismatch(PrimitiveNode node, string expected) =>
        new($"Frame type 0x{node.Type:X2} is not {expected}.");
}

/// <summary>
/// Maps signed integers onto unsigned ones so that small magnitudes stay small.
/// </summary>
/// <remarks>
/// Without it, every negative number sets the high bit and costs the full ten varint bytes.
/// </remarks>
internal static class ZigZag
{
    internal static ulong Encode(long value) => (ulong)((value << 1) ^ (value >> 63));

    internal static long Decode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
}
