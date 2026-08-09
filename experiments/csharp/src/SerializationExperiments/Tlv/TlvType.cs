namespace SerializationExperiments.Tlv;

/// <summary>
/// How a frame's payload is delimited, read from the high nibble of its Type byte.
/// </summary>
/// <remarks>
/// The whole point of putting this in the Type byte is that a reader can step over a frame
/// whose type it has never heard of. Without it, every frame would need a Length, which is a
/// wasted byte on anything of implied size — a <c>double</c> is always eight bytes.
/// </remarks>
internal enum PayloadShape
{
    /// <summary>A varint length follows the Type byte, then that many bytes.</summary>
    LengthPrefixed,

    /// <summary>No payload at all; the Type byte is the whole frame.</summary>
    Empty,

    /// <summary>One canonical varint, self-delimiting.</summary>
    Varint,

    /// <summary>A payload of exactly <see cref="TlvType.FixedWidthOf"/> bytes.</summary>
    Fixed,

    /// <summary>A varint subtype, a varint length, then that many bytes.</summary>
    Extension,

    /// <summary>Unallocatable: a reader cannot determine the width, so it must reject.</summary>
    Reserved,
}

/// <summary>
/// The Type byte of a TLV frame: a shape nibble and a type nibble.
/// </summary>
/// <remarks>
/// <para>
/// The high nibble says how to skip the frame, the low nibble says what it is. Sixteen types
/// per shape, and an unknown one is still skippable — which is what lets a document written
/// against a later version of this format pass through a reader written today unchanged.
/// </para>
/// <para>
/// Every code that predates this split is length-prefixed, so all five sit in shape 0 and
/// none of them moved.
/// </para>
/// </remarks>
internal static class TlvType
{
    // ---- shape 0x0_ : varint length prefix ----

    /// <summary>Deliberately unused, so a zero byte is never a valid Type.</summary>
    internal const byte Reserved = 0x00;

    /// <summary>Constructed: a name reference, an optional literal, then child frames.</summary>
    internal const byte Element = 0x01;

    /// <summary>Primitive: UTF-8 bytes. Assigns the next value id.</summary>
    internal const byte Text = 0x02;

    /// <summary>
    /// Primitive: a varint id of a value defined earlier by a <see cref="Text"/> frame.
    /// </summary>
    /// <remarks>
    /// A separate type rather than a discriminator inside <see cref="Text"/>, so a value
    /// that never repeats costs exactly what it did before interning existed. Over k
    /// occurrences of an L-byte value the saving is (k-1)(L-1).
    /// </remarks>
    internal const byte TextRef = 0x03;

    /// <summary>Primitive: UTF-8 bytes that claim no value id.</summary>
    internal const byte TextOnce = 0x04;

    /// <summary>
    /// Constructed: a type-name reference, an optional literal, then exactly one child frame.
    /// </summary>
    internal const byte Typed = 0x05;

    /// <summary>Primitive: raw octets.</summary>
    internal const byte Bytes = 0x06;

    /// <summary>
    /// Constructed: exactly one value frame, whose value claims the next value id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Text"/> can say "this value claims an id" in its own type code because it
    /// has <see cref="TextOnce"/> to contrast with. A fixed-width value has no spare code per
    /// type to spend on the same distinction — there would have to be a quiet twin of every
    /// primitive — so the claim is hoisted into a wrapper instead.
    /// </para>
    /// <para>
    /// Two bytes on the first occurrence buys a three-byte reference on every later one. For a
    /// <see cref="Guid"/> that is 17 bytes down to 3, which is the difference between losing to
    /// protobuf by 29% on identifier-heavy data and matching it.
    /// </para>
    /// </remarks>
    internal const byte Intern = 0x0C;

    // ---- shape 0x1_ : no payload ----

    /// <summary>The absence of a value.</summary>
    internal const byte Null = 0x10;

    /// <summary>Boolean false.</summary>
    internal const byte False = 0x11;

    /// <summary>Boolean true.</summary>
    internal const byte True = 0x12;

    // ---- shape 0x2_ : one varint ----

    /// <summary>An unsigned integer of any width, as a canonical varint.</summary>
    internal const byte UInt = 0x20;

    /// <summary>
    /// A signed integer of any width: ZigZag, then a canonical varint.
    /// </summary>
    /// <remarks>
    /// ZigZag maps -1 to 1 and -2 to 3, so small negatives cost one byte. Two's complement
    /// would set the high bit and spend all ten bytes on every negative number, which is why
    /// protobuf keeps <c>sint64</c> separate from <c>int64</c>.
    /// </remarks>
    internal const byte SInt = 0x21;

    // ---- fixed-width shapes ----

    /// <summary>IEEE 754 binary32, little-endian.</summary>
    internal const byte Float32 = 0x52;

    /// <summary>IEEE 754 binary64, little-endian.</summary>
    internal const byte Float64 = 0x62;

    /// <summary>A 16-byte UUID, in <see cref="System.Guid"/>'s own byte order.</summary>
    internal const byte Guid = 0x70;

    // ---- shape 0xF_ : extension ----

    /// <summary>Extension whose subtype numbers this format allocates.</summary>
    internal const byte Extension = 0xF0;

    /// <summary>Extension whose subtype numbers this format will never allocate.</summary>
    internal const byte PrivateExtension = 0xF1;

    /// <summary>The shape nibble of <paramref name="type"/>.</summary>
    /// <param name="type">A Type byte.</param>
    /// <returns>How the frame's payload is delimited.</returns>
    internal static PayloadShape ShapeOf(byte type) => (type >> 4) switch
    {
        0x0 => PayloadShape.LengthPrefixed,
        0x1 => PayloadShape.Empty,
        0x2 => PayloadShape.Varint,
        0x3 or 0x4 or 0x5 or 0x6 or 0x7 or 0x8 or 0x9 or 0xA => PayloadShape.Fixed,
        0xF => PayloadShape.Extension,

        // 0xB..0xE carry no width, so nothing can ever be allocated there — a reader written
        // today could not skip it, and allocating it later is exactly the ossification this
        // split exists to avoid.
        _ => PayloadShape.Reserved,
    };

    /// <summary>Payload width in bytes, for a <see cref="PayloadShape.Fixed"/> type.</summary>
    /// <param name="type">A Type byte whose shape is <see cref="PayloadShape.Fixed"/>.</param>
    /// <returns>The exact payload width.</returns>
    internal static int FixedWidthOf(byte type) => 1 << ((type >> 4) - 0x3);

    /// <summary>Whether the codec understands <paramref name="type"/>, rather than merely
    /// being able to skip it.</summary>
    /// <param name="type">A Type byte.</param>
    /// <returns><see langword="true"/> if a node can be built from it.</returns>
    internal static bool IsKnown(byte type) => type is
        Element or Text or TextRef or TextOnce or Typed or Bytes or Intern or
        Null or False or True or UInt or SInt or Float32 or Float64 or Guid;
}
