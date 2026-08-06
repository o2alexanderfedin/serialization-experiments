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

    /// <summary>Primitive: UTF-8 bytes.</summary>
    internal const byte Text = 0x02;

    /// <summary>Deliberately unused, so a zero byte is never a valid Type.</summary>
    internal const byte Reserved = 0x00;
}
