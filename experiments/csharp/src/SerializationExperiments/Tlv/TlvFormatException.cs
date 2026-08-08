namespace SerializationExperiments.Tlv;

/// <summary>
/// Thrown when a byte stream does not conform to the TLV format.
/// </summary>
public sealed class TlvFormatException : Exception
{
    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="message">Description of the fault, including the byte offset where possible.</param>
    public TlvFormatException(string message)
        : base(message)
    {
    }
}
