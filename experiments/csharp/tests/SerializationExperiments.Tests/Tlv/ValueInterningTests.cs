using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Text values are interned the same way element names are: the first occurrence carries
/// the literal and assigns the next id, later occurrences are a short reference.
/// </summary>
/// <remarks>
/// References use their own type code rather than a discriminator field inside
/// <c>TEXT</c>, so a value that never repeats costs exactly what it cost before interning
/// existed. Over k occurrences of an L-byte value the saving is (k-1)(L-1), which is never
/// negative — there is no threshold to tune and no shape that regresses.
/// </remarks>
public sealed class ValueInterningTests
{
    [Fact]
    public void Repeated_value_is_encoded_once()
    {
        Node tree = Element("root", Element("a", Text("repeated")), Element("b", Text("repeated")));

        byte[] encoded = TlvEncoder.Encode(tree);

        // "repeated" (8 bytes) appears once in the output, not twice.
        Assert.Equal(1, CountOccurrences(encoded, "repeated"u8));
    }

    [Fact]
    public void Reference_costs_three_bytes()
    {
        Node once = Element("root", Element("a", Text("some long-ish value")));
        Node twice = Element("root", Element("a", Text("some long-ish value")), Element("b", Text("some long-ish value")));

        // The second <b> element frame is 3 bytes of element header plus a 3-byte TEXT_REF.
        long elementOverhead = TlvEncoder.Measure(Element("root", Element("a", Text("x")), Element("b", Text("x"))))
                             - TlvEncoder.Measure(Element("root", Element("a", Text("x"))));

        Assert.Equal(elementOverhead, TlvEncoder.Measure(twice) - TlvEncoder.Measure(once));
    }

    [Fact]
    public void A_value_that_never_repeats_costs_what_it_always_did()
    {
        // Distinct values must not pay for the interning mechanism. These bytes are the
        // documented example, unchanged by this feature.
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

        Node tree = Element("order", Element("line", Text("a")), Element("line", Text("b")));

        Assert.Equal(documented, TlvEncoder.Encode(tree));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(50)]
    public void Saving_grows_with_repetition(int occurrences)
    {
        const string value = "a value worth interning";

        long interned = TlvEncoder.Measure(Repeat(value, occurrences));
        long ifEachWereDistinct = TlvEncoder.Measure(DistinctOfSameLength(value.Length, occurrences));

        Assert.True(
            interned < ifEachWereDistinct,
            $"{occurrences} repeats: interned {interned} should beat distinct {ifEachWereDistinct}");
    }

    [Fact]
    public void Round_trips_with_repeated_values()
    {
        Node tree = Element(
            "catalogue",
            Element("item", Text("shared")),
            Element("item", Text("unique-one")),
            Element("item", Text("shared")),
            Element("item", Text("shared")),
            Element("item", Text("unique-two")));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void Round_trips_when_every_value_is_identical()
    {
        Node tree = Repeat("same", 25);

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void Empty_values_round_trip()
    {
        Node tree = Element("root", Element("a", Text(string.Empty)), Element("b", Text(string.Empty)));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void Re_encoding_a_decoded_document_is_byte_identical()
    {
        // Canonical encoding: always reference a value already seen, never re-emit it.
        byte[] once = TlvEncoder.Encode(Repeat("value", 10));

        Assert.Equal(once, TlvEncoder.Encode(TlvDecoder.Decode(once)));
    }

    [Fact]
    public void A_referenced_value_decodes_to_the_same_string_instance_by_default()
    {
        // A reference needs no UTF-8 decode and no new allocation.
        Node decoded = TlvDecoder.Decode(TlvEncoder.Encode(Repeat("shared value", 3)));

        List<string> texts = [];
        Collect(decoded, texts);

        Assert.Equal(3, texts.Count);
        Assert.Same(texts[0], texts[1]);
        Assert.Same(texts[1], texts[2]);
    }

    [Fact]
    public void Sharing_can_be_turned_off_for_distinct_instances()
    {
        byte[] encoded = TlvEncoder.Encode(Repeat("shared value", 3));

        Node decoded = TlvDecoder.Decode(encoded, new TlvDecoderOptions { ShareValueInstances = false });

        List<string> texts = [];
        Collect(decoded, texts);

        Assert.Equal(3, texts.Count);
        Assert.NotSame(texts[0], texts[1]);
        Assert.NotSame(texts[1], texts[2]);
        Assert.Equal(texts[0], texts[1]);   // distinct instances, equal content
    }

    [Fact]
    public void The_option_changes_instances_only_never_content()
    {
        // It is a decode-side choice: the same bytes yield the same document either way.
        byte[] encoded = TlvEncoder.Encode(Repeat("shared value", 5));

        Node shared = TlvDecoder.Decode(encoded, new TlvDecoderOptions { ShareValueInstances = true });
        Node distinct = TlvDecoder.Decode(encoded, new TlvDecoderOptions { ShareValueInstances = false });

        Assert.Equal(Render(shared), Render(distinct));
        Assert.Equal(TlvEncoder.Encode(shared), TlvEncoder.Encode(distinct));
    }

    [Fact]
    public void Values_drawn_from_a_small_vocabulary_round_trip()
    {
        // The realistic case interning targets: enum-like values repeating across many
        // elements, interleaved rather than adjacent, so references point far back.
        string[] vocabulary = ["pending", "approved", "rejected", "cancelled"];
        Node[] children = new Node[40];
        for (int index = 0; index < children.Length; index++)
        {
            children[index] = Element("status", Text(vocabulary[index % vocabulary.Length]));
        }

        Node tree = new ElementNode("feed", children);
        byte[] encoded = TlvEncoder.Encode(tree);

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(encoded)));

        // Each distinct value is written exactly once, however far apart its uses are.
        foreach (string word in vocabulary)
        {
            Assert.Equal(1, CountOccurrences(encoded, System.Text.Encoding.UTF8.GetBytes(word)));
        }
    }

    [Fact]
    public void Interleaved_repeats_reference_the_right_values()
    {
        // a b a b a — a decoder that mixed up ids would still produce well-formed output,
        // so only comparing content catches it.
        Node tree = Element(
            "root",
            Element("x", Text("alpha")),
            Element("x", Text("beta")),
            Element("x", Text("alpha")),
            Element("x", Text("beta")),
            Element("x", Text("alpha")));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void Decoder_rejects_a_reference_to_an_undefined_value()
    {
        // ELEMENT "a" containing TEXT_REF with id 7, but no value has been defined.
        byte[] malformed = [0x01, 0x06, 0x00, 0x01, 0x61, 0x03, 0x01, 0x07];

        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
    }

    private static Node Repeat(string value, int count)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = Element("item", Text(value));
        }

        return new ElementNode("root", children);
    }

    private static Node DistinctOfSameLength(int length, int count)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            string padded = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 .PadLeft(length, 'z');
            children[index] = Element("item", Text(padded));
        }

        return new ElementNode("root", children);
    }

    private static void Collect(Node node, List<string> texts)
    {
        switch (node)
        {
            case TextNode text:
                texts.Add(text.Value);
                break;
            case ElementNode element:
                foreach (Node child in element.Children)
                {
                    Collect(child, texts);
                }

                break;
        }
    }

    private static int CountOccurrences(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        int count = 0;
        for (int index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle))
            {
                count++;
            }
        }

        return count;
    }
}
