namespace SerializationExperiments.Tlv;

/// <summary>
/// Writes bytes through to a <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// The stream is not required to be seekable: lengths are known before anything is written,
/// so nothing is ever back-patched.
/// </remarks>
public sealed class StreamSink : IByteSink
{
    private readonly Stream stream;

    /// <summary>Initializes a new instance over <paramref name="stream"/>.</summary>
    /// <param name="stream">Destination stream. Not disposed by this sink.</param>
    public StreamSink(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
    }

    /// <inheritdoc />
    public long BytesWritten { get; private set; }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> bytes)
    {
        this.stream.Write(bytes);
        this.BytesWritten += bytes.Length;
    }
}
