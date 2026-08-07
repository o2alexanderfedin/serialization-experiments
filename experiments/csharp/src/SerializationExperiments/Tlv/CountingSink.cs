namespace SerializationExperiments.Tlv;

/// <summary>
/// Discards bytes and counts them.
/// </summary>
/// <remarks>
/// Lets the encoder be run for its byte count alone, without allocating a buffer to hold
/// the result — the mechanism behind measuring a length prefix without buffering the value.
/// </remarks>
public sealed class CountingSink : IByteSink
{
    /// <inheritdoc />
    public long BytesWritten { get; private set; }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> bytes) => this.BytesWritten += bytes.Length;
}
