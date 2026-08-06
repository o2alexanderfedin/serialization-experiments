using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// The Length field is attacker-controlled input; every one of these must fail cleanly
/// rather than read out of bounds, allocate wildly, or exhaust the stack.
/// </summary>
public sealed class TlvDecoderTests
{
    [Fact]
    public void Decodes_the_documented_byte_sequence()
    {
        byte[] documented =
        [
            0x01, 0x18,
            0x00, 0x05, 0x6F, 0x72, 0x64, 0x65, 0x72,
            0x01, 0x09,
            0x00, 0x04, 0x6C, 0x69, 0x6E, 0x65,
            0x02, 0x01, 0x61,
            0x01, 0x04,
            0x02,
            0x02, 0x01, 0x62,
        ];

        Assert.Equal("<order><line>a</line><line>b</line></order>", Render(TlvDecoder.Decode(documented)));
    }

    [Fact]
    public void Rejects_an_empty_buffer()
    {
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([]));
    }

    [Fact]
    public void Rejects_an_unknown_type()
    {
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([0x7F, 0x00]));
    }

    [Fact]
    public void Rejects_the_reserved_zero_type()
    {
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([0x00, 0x00]));
    }

    [Fact]
    public void Rejects_a_length_longer_than_the_buffer()
    {
        // TEXT claiming 200 bytes with 3 present.
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([0x02, 0xC8, 0x01, 0x61, 0x62, 0x63]));
    }

    [Fact]
    public void Rejects_a_name_length_that_escapes_its_element()
    {
        // ELEMENT value length 3, but the name claims 100 bytes.
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([0x01, 0x03, 0x00, 0x64, 0x61]));
    }

    [Fact]
    public void Rejects_a_reference_to_an_undefined_name()
    {
        // ELEMENT whose NameRef is 5, with nothing defined yet.
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([0x01, 0x01, 0x05]));
    }

    [Fact]
    public void Rejects_trailing_bytes_after_the_root()
    {
        byte[] valid = TlvEncoder.Encode(Element("a", Text("b")));
        byte[] withTrailer = [.. valid, 0x00];

        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(withTrailer));
    }

    [Fact]
    public void Rejects_a_truncated_document()
    {
        byte[] valid = TlvEncoder.Encode(Element("order", Element("line", Text("a"))));

        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(valid.AsSpan(0, valid.Length - 1)));
    }

    [Fact]
    public void Rejects_nesting_deeper_than_the_limit()
    {
        // Each frame is ELEMENT, length, NameRef=0, NameLen=0 — a nameless element wrapping
        // the next. Cheap to construct, unbounded in depth: the stack-exhaustion shape.
        const int depth = 2_000;
        byte[] document = new byte[depth * 4];
        for (int level = 0; level < depth; level++)
        {
            int offset = level * 4;
            document[offset] = 0x01;
            document[offset + 1] = (byte)((depth - level - 1) * 4 + 2);
            document[offset + 2] = 0x00;
            document[offset + 3] = 0x00;
        }

        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(document));
    }
}
