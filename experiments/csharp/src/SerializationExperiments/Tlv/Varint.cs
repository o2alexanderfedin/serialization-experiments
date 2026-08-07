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
    /// The buffer ends mid-value, or the encoding is longer than 64 bits allows.
    /// </exception>
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
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new TlvFormatException($"Varint ending at offset {offset} exceeds {MaxBytes} bytes.");
    }
}
