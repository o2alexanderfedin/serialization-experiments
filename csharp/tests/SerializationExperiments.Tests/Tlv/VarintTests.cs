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

    private static byte[] Encode(ulong value)
    {
        using var buffer = new MemoryStream();
        Varint.Write(value, new StreamSink(buffer));
        return buffer.ToArray();
    }
}
