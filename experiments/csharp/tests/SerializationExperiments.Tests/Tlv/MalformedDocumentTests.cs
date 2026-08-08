using SerializationExperiments.Tlv;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Hand-rolled documents whose lengths disagree with their contents.
/// </summary>
/// <remarks>
/// The encoder cannot produce any of these, so they have to be written byte by byte. Each
/// one is well-formed right up to the frame under test, so a decoder that trusted a declared
/// length would return a plausible tree rather than an error — which is the failure mode
/// worth having tests for.
/// </remarks>
public sealed class MalformedDocumentTests
{
    [Fact]
    public void A_value_reference_that_does_not_fill_its_length_is_rejected()
    {
        byte[] malformed =
        [
            0x01, 0x0B,                     // ELEMENT, 11 bytes of value
            0x00, 0x01, (byte)'a',          // name literal "a"
            0x02, 0x02, (byte)'x', (byte)'x', // TEXT "xx" -> claims value id 0
            0x03, 0x02, 0x00, 0x00,         // TEXT_REF declaring 2 bytes, id varint uses 1
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("Value reference frame", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_name_running_past_its_frame_is_rejected()
    {
        byte[] malformed =
        [
            0x05, 0x05,                     // TYPED, 5 bytes of value
            0x00, 0x09, (byte)'T',          // TypeRef 0, then a name claiming 9 bytes
            0x00, 0x00,
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("past the end of its frame", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_frames_overrunning_their_element_are_rejected()
    {
        byte[] malformed =
        [
            0x01, 0x08,                     // ELEMENT declaring 8 bytes of value...
            0x00, 0x01, (byte)'a',          // name literal "a"      (3 bytes)
            0x04, 0x05,                     // ...but its one child needs 7, for 10 in all
            (byte)'x', (byte)'x', (byte)'x', (byte)'x', (byte)'x',
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("overran their element", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_element_name_running_past_its_frame_is_rejected()
    {
        byte[] malformed =
        [
            0x01, 0x05,
            0x00, 0x09, (byte)'a',
            0x00, 0x00,
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("past the end of its element", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_node_type_is_refused_by_the_encoder()
    {
        // Node is a public abstract record, so anyone can add a case the codec has never
        // heard of. It must be told so, rather than silently encoding nothing.
        ArgumentException error =
            Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(new UnknownNode()));

        Assert.Contains("Unsupported node type", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UnknownNode), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_node_type_is_refused_wherever_it_is_nested()
    {
        Node tree = new ElementNode("root", [new ElementNode("a", [new UnknownNode()])]);

        Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(tree));
        Assert.Throws<ArgumentException>(() => TlvEncoder.Measure(tree));
        Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(tree, new CountingSink()));
    }

    [Fact]
    public void An_unsupported_node_type_writes_nothing_before_it_fails()
    {
        // The rejection happens in the counting pass, before the sink is touched, so a
        // caller is not left with half a document on a stream they cannot rewind.
        var sink = new CountingSink();

        Assert.Throws<ArgumentException>(
            () => TlvEncoder.Encode(new ElementNode("root", [new UnknownNode()]), sink));
        Assert.Equal(0, sink.BytesWritten);
    }

    /// <summary>A node kind the codec knows nothing about.</summary>
    private sealed record UnknownNode : Node;
}
