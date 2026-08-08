namespace SerializationExperiments.Tlv;

/// <summary>
/// The Type byte of a TLV frame.
/// </summary>
/// <remarks>
/// Two entries, regardless of how many distinct XML tag names arrive: the Type carries the
/// <em>category</em> of node, while identity lives in the value as an interned name.
/// </remarks>
internal static class TlvType
{
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
    /// occurrences of an L-byte value the saving is (k-1)(L-1) — never negative, so there
    /// is no threshold to tune.
    /// </remarks>
    internal const byte TextRef = 0x03;

    /// <summary>Deliberately unused, so a zero byte is never a valid Type.</summary>
    internal const byte Reserved = 0x00;
}
