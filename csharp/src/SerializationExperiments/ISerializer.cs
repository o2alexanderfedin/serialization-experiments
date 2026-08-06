namespace SerializationExperiments;

/// <summary>
/// A uniform surface over one serialization format.
/// </summary>
/// <remarks>
/// Experiments compare candidates through this interface so the measurement code never
/// depends on which library is underneath. Implementations are expected to be stateless
/// and safe to reuse across iterations of a benchmark — constructing them per call would
/// measure setup cost rather than encode/decode cost.
/// </remarks>
public interface ISerializer
{
    /// <summary>
    /// Label used to identify this serializer in experiment results.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Encodes <paramref name="value"/> into a freshly allocated buffer.
    /// </summary>
    /// <typeparam name="T">Type being encoded.</typeparam>
    /// <param name="value">Value to encode.</param>
    /// <returns>The encoded payload.</returns>
    byte[] Serialize<T>(T value);

    /// <summary>
    /// Decodes a payload previously produced by <see cref="Serialize{T}"/>.
    /// </summary>
    /// <typeparam name="T">Type being decoded.</typeparam>
    /// <param name="data">Encoded payload.</param>
    /// <returns>The decoded value, never <see langword="null"/>.</returns>
    /// <exception cref="SerializationFailedException">
    /// The payload is malformed, or decodes to null.
    /// </exception>
    T Deserialize<T>(ReadOnlySpan<byte> data);
}
