namespace SerializationExperiments.Tlv;

/// <summary>
/// Writes into a caller-supplied array whose size is already known.
/// </summary>
/// <remarks>
/// The measuring pass computes the exact encoded length before a byte is emitted, so the
/// output array can be allocated once at exactly that size — no growth chain and no final
/// copy. A write that would run past the end throws rather than growing, because overrunning
/// means the two passes disagreed, which is a corrupt document rather than a full buffer.
/// </remarks>
internal sealed class BufferSink : IByteSink
{
    private readonly byte[] buffer;
    private int position;

    /// <summary>Initializes a new instance over <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Destination, sized to the measured length.</param>
    internal BufferSink(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        this.buffer = buffer;
    }

    /// <inheritdoc />
    public long BytesWritten => this.position;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The write would run past the measured length.
    /// </exception>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > this.buffer.Length - this.position)
        {
            throw new InvalidOperationException(
                $"Encoder overran its measured length: {bytes.Length} byte(s) at offset " +
                $"{this.position} of a {this.buffer.Length}-byte buffer.");
        }

        bytes.CopyTo(this.buffer.AsSpan(this.position));
        this.position += bytes.Length;
    }
}
