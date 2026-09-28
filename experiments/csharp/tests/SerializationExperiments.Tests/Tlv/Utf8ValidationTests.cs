using SerializationExperiments.Tlv;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Text, names and type names must be well-formed UTF-8.
/// </summary>
/// <remarks>
/// A lenient UTF-8 decode turns every malformed sequence into U+FFFD. The bytes <c>FF</c> and
/// the bytes <c>EF BF BD</c> would then decode to the same string, so two different documents
/// read back as one — the same one-document-one-encoding break that padded varints and
/// non-canonical NaNs are rejected for — and re-encoding the result does not give back the
/// bytes that arrived.
/// </remarks>
public sealed class Utf8ValidationTests
{
    [Fact]
    public void A_text_value_that_is_not_valid_utf8_is_rejected()
    {
        byte[] malformed = [0x02, 0x01, 0xFF]; // TEXT, 1 byte: 0xFF never occurs in UTF-8

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("not valid UTF-8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_text_once_value_with_an_overlong_encoding_is_rejected()
    {
        byte[] malformed = [0x04, 0x02, 0xC0, 0x80]; // TEXT_ONCE, overlong NUL

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("not valid UTF-8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_element_name_that_is_not_valid_utf8_is_rejected()
    {
        byte[] malformed =
        [
            0x01, 0x03,                     // ELEMENT, 3 bytes of value
            0x00, 0x01, 0xFF,               // name literal of one invalid byte, no children
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("not valid UTF-8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_name_holding_an_encoded_surrogate_is_rejected()
    {
        byte[] malformed =
        [
            0x05, 0x07,                     // TYPED, 7 bytes of value
            0x00, 0x03, 0xED, 0xA0, 0x80,   // name literal: U+D800 encoded as UTF-8
            0x04, 0x00,                     // TEXT_ONCE "" as the inner value
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("not valid UTF-8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_genuine_replacement_character_still_decodes_and_re_encodes_exactly()
    {
        // U+FFFD is a real character. Rejecting malformed input must not reject it.
        byte[] document = [0x04, 0x03, 0xEF, 0xBF, 0xBD]; // TEXT_ONCE "\uFFFD"

        Node decoded = TlvDecoder.Decode(document);

        Assert.Equal(new TextNode("\uFFFD"), decoded);
        Assert.Equal(document, TlvEncoder.Encode(decoded));
    }
}
