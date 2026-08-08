using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Text values are interned like element names, but only where it pays: a value that will
/// recur carries a literal that claims the next id and is referenced thereafter, while a
/// value seen once carries a literal that claims nothing.
/// </summary>
/// <remarks>
/// <para>
/// References use their own type code rather than a discriminator field inside <c>TEXT</c>,
/// so a value that never repeats costs exactly what it cost before interning existed. Over
/// k occurrences of an L-byte value the saving is (k-1)(L-1), never negative.
/// </para>
/// <para>
/// The reason a third literal code exists is that (k-1)(L-1) is zero, not positive, when k
/// is 1 — and an id claimed for no gain is not free, because it pushes every later
/// reference closer to a second varint byte. <c>TEXT_ONCE</c> keeps the id space dense. The
/// decoder tells the two apart by type code alone, so the rule that produced them stays
/// entirely on the encoder's side and can change without a format break.
/// </para>
/// </remarks>
public sealed class ValueInterningTests
{
    /// <summary>A literal that claims the next value id.</summary>
    private const byte TextLiteral = TlvType.Text;

    /// <summary>A literal that claims nothing.</summary>
    private const byte TextOnce = TlvType.TextOnce;

    /// <summary>A reference to a value already defined.</summary>
    private const byte TextRef = TlvType.TextRef;

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
        // Distinct values must not pay for the interning mechanism. Same 26 bytes as
        // before interning existed; only the type code says these literals claim no id.
        byte[] documented =
        [
            0x01, 0x18,
            0x00, 0x05, 0x6F, 0x72, 0x64, 0x65, 0x72,
            0x01, 0x09,
            0x00, 0x04, 0x6C, 0x69, 0x6E, 0x65,
            0x04, 0x01, 0x61,
            0x01, 0x04,
            0x02,
            0x04, 0x01, 0x62,
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

    [Fact]
    public void A_value_seen_once_claims_no_id()
    {
        // TEXT_ONCE (0x04) rather than TEXT (0x02): same bytes on the wire, but it adds
        // nothing to the value table.
        List<byte> types = Frames.TypeCodes(TlvEncoder.Encode(Element("root", Element("a", Text("solo")))));

        Assert.Contains(TextOnce, types);
        Assert.DoesNotContain(TextLiteral, types);
    }

    [Fact]
    public void A_value_seen_twice_claims_one()
    {
        List<byte> types = Frames.TypeCodes(TlvEncoder.Encode(
            Element("root", Element("a", Text("twice")), Element("b", Text("twice")))));

        Assert.Equal(1, types.Count(type => type == TextLiteral));
        Assert.Equal(1, types.Count(type => type == TextRef));
        Assert.DoesNotContain(TextOnce, types);
    }

    [Fact]
    public void The_cost_of_a_repeating_tail_does_not_depend_on_what_precedes_it()
    {
        // The whole point of the distinction, stated as a property. Under the previous rule
        // every literal claimed an id, so a long head of distinct values pushed the tail's
        // value past 127 and each of its references widened from one varint byte to two.
        // Values seen once now claim nothing, so the head cannot reach the tail's cost.
        // Stated directly on the wire rather than by subtracting sizes, because the root
        // frame's own length varint widens as the document grows and would confound that.
        List<Frames.Frame> frames = Frames.Walk(TlvEncoder.Encode(HeadAndTail(head: 300, tail: 50)));
        List<Frames.Frame> references = frames.Where(frame => frame.Type == TextRef).ToList();

        Assert.Equal(49, references.Count);
        Assert.All(references, reference => Assert.Equal(1, reference.IdWidth));
        Assert.All(references, reference => Assert.Equal(0UL, reference.ValueId));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(300)]
    public void A_document_with_a_long_distinct_head_round_trips(int head)
    {
        Node tree = HeadAndTail(head, tail: 50);

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void A_reference_after_many_unreferenced_literals_still_resolves()
    {
        // A decoder that registered TEXT_ONCE literals would resolve every id to the wrong
        // value here, while still producing structurally valid output.
        Node[] children = new Node[120];
        for (int index = 0; index < 100; index++)
        {
            children[index] = Element("item", Text($"noise-{index:D4}"));
        }

        for (int index = 100; index < 120; index++)
        {
            children[index] = Element("item", Text(index % 2 == 0 ? "alpha-value" : "beta-value"));
        }

        Node tree = new ElementNode("root", children);

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void A_repeated_one_byte_value_claims_no_id_either()
    {
        // Repetition alone is not enough: (k-1)(L-1) is zero at L=1, so an id would be spent
        // for no gain and would push later references wider.
        List<byte> types = Frames.TypeCodes(TlvEncoder.Encode(
            Element("root", Element("a", Text("x")), Element("b", Text("x")), Element("c", Text("x")))));

        Assert.DoesNotContain(TextLiteral, types);
        Assert.DoesNotContain(TextRef, types);
        Assert.Equal(3, types.Count(type => type == TextOnce));
    }

    private static long SizeOf(int head, int tail) => TlvEncoder.Measure(HeadAndTail(head, tail));

    /// <summary>
    /// <paramref name="head"/> elements with all-distinct values, then <paramref name="tail"/>
    /// elements all sharing one value.
    /// </summary>
    private static Node HeadAndTail(int head, int tail)
    {
        Node[] children = new Node[head + tail];
        for (int index = 0; index < head; index++)
        {
            children[index] = Element("item", Text($"distinct-{index:D4}"));
        }

        for (int index = 0; index < tail; index++)
        {
            children[head + index] = Element("item", Text("recurring"));
        }

        return new ElementNode("root", children);
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
            case TypedNode typed:
                // Without this arm a typed subtree would be skipped in silence, and any test
                // using this helper on one would pass by collecting nothing.
                Collect(typed.Inner, texts);
                break;

            case ElementNode element:
                foreach (Node child in element.Children)
                {
                    Collect(child, texts);
                }

                break;

            default:
                throw new InvalidOperationException($"Unhandled node type {node.GetType()}.");
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
