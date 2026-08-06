namespace SerializationExperiments;

/// <summary>
/// Thrown when a serializer cannot encode or decode a value.
/// </summary>
/// <remarks>
/// Each underlying library reports failure differently — <c>JsonException</c>,
/// <c>MessagePackSerializationException</c>, and so on. Wrapping them in one type lets an
/// experiment record "this candidate rejected this payload" uniformly across formats.
/// </remarks>
public sealed class SerializationFailedException : Exception
{
    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="message">Description of the failure.</param>
    public SerializationFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and the originating error.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">The format-specific exception that caused this failure.</param>
    public SerializationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
