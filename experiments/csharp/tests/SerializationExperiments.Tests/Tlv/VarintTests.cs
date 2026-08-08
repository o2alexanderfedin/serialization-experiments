using SerializationExperiments.Tlv;

namespace SerializationExperiments.Tests.Tlv;

public sealed class VarintTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16_383UL, 2)]
    [InlineData(16_384UL, 3)]
    [InlineData(2_097_151UL, 3)]
    [InlineData(2_097_152UL, 4)]
    [InlineData(ulong.MaxValue, 10)]
    public void Size_matches_the_documented_boundaries(ulong value, int expected)
    {
        Assert.Equal(expected, Varint.Size(value));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData(16_383UL)]
    [InlineData(16_384UL)]
    [InlineData(uint.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void Round_trips_through_write_and_read(ulong value)
    {
        byte[] encoded = Encode(value);
        int offset = 0;

        Assert.Equal(Varint.Size(value), encoded.Length);
        Assert.Equal(value, Varint.Read(encoded, ref offset));
        Assert.Equal(encoded.Length, offset);
    }

    [Fact]
    public void Write_uses_little_endian_seven_bit_groups()
    {
        // 300 = 0b1_0010_1100 -> groups 0101100 then 0000010 -> 0xAC 0x02
        Assert.Equal(new byte[] { 0xAC, 0x02 }, Encode(300));
    }

    [Fact]
    public void Read_rejects_a_value_that_runs_past_the_buffer()
    {
        // Continuation bit set on the final byte: the value never terminates.
        Assert.Throws<TlvFormatException>(() =>
        {
            int offset = 0;
            Varint.Read([0x80], ref offset);
        });
    }

    [Fact]
    public void Read_rejects_an_overlong_encoding()
    {
        byte[] elevenBytes = [.. Enumerable.Repeat((byte)0x80, 10), (byte)0x01];

        Assert.Throws<TlvFormatException>(() =>
        {
            int offset = 0;
            Varint.Read(elevenBytes, ref offset);
        });
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x00 })]                                     // 0 in two bytes
    [InlineData(new byte[] { 0x81, 0x00 })]                                     // 1 in two bytes
    [InlineData(new byte[] { 0xFF, 0x00 })]                                     // 127 in two bytes
    [InlineData(new byte[] { 0x80, 0x80, 0x00 })]                               // 0 in three
    [InlineData(new byte[] { 0x80, 0x81, 0x00 })]                               // 128 in three
    public void Read_rejects_a_padded_encoding(byte[] padded)
    {
        // LEB128 lets the same number be written several ways. Accepting more than the
        // shortest would give a document more than one valid byte representation, which
        // breaks canonical re-encoding and, where the bytes are hashed or signed, is the
        // BER/DER signature-bypass shape.
        Assert.Throws<TlvFormatException>(() =>
        {
            int offset = 0;
            Varint.Read(padded, ref offset);
        });
    }

    [Fact]
    public void Read_accepts_the_shortest_encoding_of_the_same_values()
    {
        // The control for the case above: these are the canonical forms of 0, 1, 127 and 128.
        Assert.Equal(0UL, ReadAll([0x00]));
        Assert.Equal(1UL, ReadAll([0x01]));
        Assert.Equal(127UL, ReadAll([0x7F]));
        Assert.Equal(128UL, ReadAll([0x80, 0x01]));
    }

    [Fact]
    public void Read_rejects_a_value_that_does_not_fit_in_64_bits()
    {
        // The tenth byte carries only bit 63. Payload bits above it used to be shifted off
        // the end and dropped, so several encodings read back as the same number.
        byte[] tooLarge = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F];

        Assert.Throws<TlvFormatException>(() =>
        {
            int offset = 0;
            Varint.Read(tooLarge, ref offset);
        });
    }

    [Fact]
    public void Read_accepts_the_largest_value_that_does_fit()
    {
        // ulong.MaxValue: nine full groups then bit 63 alone.
        byte[] largest = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];

        Assert.Equal(ulong.MaxValue, ReadAll(largest));
        Assert.Equal(largest, Encode(ulong.MaxValue));
    }

    [Fact]
    public void A_document_with_a_padded_length_is_rejected()
    {
        // The reason this matters, end to end: the documented example with its root Length
        // written as [98 00] instead of [18]. It used to decode and then re-encode one byte
        // shorter, so a document had two valid encodings.
        byte[] padded =
        [
            0x01, 0x98, 0x00,
            0x00, 0x05, 0x6F, 0x72, 0x64, 0x65, 0x72,
            0x01, 0x09,
            0x00, 0x04, 0x6C, 0x69, 0x6E, 0x65,
            0x04, 0x01, 0x61,
            0x01, 0x04,
            0x02,
            0x04, 0x01, 0x62,
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(padded));
        Assert.Contains("padded", error.Message, StringComparison.Ordinal);
    }

    private static ulong ReadAll(byte[] data)
    {
        int offset = 0;
        ulong value = Varint.Read(data, ref offset);
        Assert.Equal(data.Length, offset);
        return value;
    }

    private static byte[] Encode(ulong value)
    {
        using var buffer = new MemoryStream();
        Varint.Write(value, new StreamSink(buffer));
        return buffer.ToArray();
    }
}
