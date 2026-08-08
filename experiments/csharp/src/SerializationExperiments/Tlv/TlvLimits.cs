namespace SerializationExperiments.Tlv;

/// <summary>
/// Bounds that are part of the format rather than of either codec.
/// </summary>
public static class TlvLimits
{
    /// <summary>
    /// Deepest frame nesting a document may contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both codecs recurse once per frame, and a <see cref="StackOverflowException"/> cannot
    /// be caught — it takes the process down. The decoder therefore has to bound depth
    /// because it reads bytes it did not produce.
    /// </para>
    /// <para>
    /// The encoder enforces the same bound, which is the point of stating it here rather than
    /// privately in the decoder. An encoder without a limit produces documents its own
    /// decoder refuses, which is a broken round-trip contract that only shows up at the far
    /// end of the wire. Rejecting during the measuring pass means nothing is emitted before
    /// the failure.
    /// </para>
    /// <para>
    /// The root frame sits at depth 0, so a document may nest this many frames beneath it.
    /// </para>
    /// </remarks>
    public const int MaxDepth = 512;
}
