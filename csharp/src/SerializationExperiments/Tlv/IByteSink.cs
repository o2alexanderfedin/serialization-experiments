namespace SerializationExperiments.Tlv;

/// <summary>
/// Destination for encoded bytes.
/// </summary>
/// <remarks>
/// The abstraction exists so the same emit code can run against a real stream or against a
/// counter. See <see cref="CountingSink"/>.
/// </remarks>
public interface IByteSink
{
    /// <summary>Total bytes handed to this sink since construction.</summary>
    long BytesWritten { get; }

    /// <summary>Appends bytes to the sink.</summary>
    /// <param name="bytes">Bytes to append.</param>
    void Write(ReadOnlySpan<byte> bytes);
}
