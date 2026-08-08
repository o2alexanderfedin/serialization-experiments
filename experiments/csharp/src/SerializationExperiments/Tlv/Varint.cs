namespace SerializationExperiments.Tlv;

/// <summary>
/// Unsigned LEB128: seven payload bits per byte, least-significant group first, high bit set
/// on every byte except the last.
/// </summary>
internal static class Varint
{
    /// <summary>Largest number of bytes a 64-bit value can occupy (ceil(64/7)).</summary>
    internal const int MaxBytes = 10;

    /// <summary>Bytes required to encode <paramref name="value"/>.</summary>
    /// <param name="value">Value to size.</param>
    /// <returns>Encoded length in bytes, at least 1.</returns>
    internal static int Size(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }

    /// <summary>Appends <paramref name="value"/> to <paramref name="sink"/>.</summary>
    /// <param name="value">Value to encode.</param>
    /// <param name="sink">Destination.</param>
    internal static void Write(ulong value, IByteSink sink)
    {
        Span<byte> buffer = stackalloc byte[MaxBytes];
        int count = 0;
        while (value >= 0x80)
        {
            buffer[count++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[count++] = (byte)value;
        sink.Write(buffer[..count]);
    }

    /// <summary>Reads a value from <paramref name="data"/>, advancing <paramref name="offset"/>.</summary>
    /// <param name="data">Buffer to read from.</param>
    /// <param name="offset">Position to read at; advanced past the value.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="TlvFormatException">
    /// The buffer ends mid-value, the encoding is longer than 64 bits allows, or the value is
    /// padded into more bytes than it needs.
    /// </exception>
    /// <remarks>
    /// Only the shortest encoding of a value is accepted. LEB128 permits padding — `80 00`
    /// and `00` both denote zero — and accepting both would mean a document has more than one
    /// valid byte representation. That breaks re-encoding a decoded document byte-for-byte,
    /// and, wherever these bytes are hashed or signed, it is the BER/DER signature-bypass
    /// shape: two encodings that verify as one document.
    /// </remarks>
    internal static ulong Read(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        int shift = 0;

        for (int consumed = 0; consumed < MaxBytes; consumed++)
        {
            if (offset >= data.Length)
            {
                throw new TlvFormatException($"Varint at offset {offset} runs past the end of the buffer.");
            }

            byte current = data[offset++];

            // The tenth byte contributes only bit 63; any payload bit above it would be
            // shifted off the end and silently discarded, so two different encodings would
            // read back as the same number.
            if (shift == 63 && (current & 0x7E) != 0)
            {
                throw new TlvFormatException(
                    $"Varint ending at offset {offset} does not fit in 64 bits.");
            }

            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                // A final group of zero means the value was padded: the same number fits in
                // fewer bytes, so this is not the canonical encoding of it.
                if (consumed > 0 && current == 0)
                {
                    throw new TlvFormatException(
                        $"Varint ending at offset {offset} is padded; {value} encodes in " +
                        $"{Size(value)} byte(s), not {consumed + 1}.");
                }

                return value;
            }

            shift += 7;
        }

        throw new TlvFormatException($"Varint ending at offset {offset} exceeds {MaxBytes} bytes.");
    }
}
