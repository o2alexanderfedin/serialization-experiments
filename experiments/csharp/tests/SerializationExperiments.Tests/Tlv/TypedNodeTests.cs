using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Type names ride the same dynamic-interning mechanism as element names, in a table of
/// their own, and cost nothing to documents that carry none.
/// </summary>
public sealed class TypedNodeTests
{
    private const byte TypedFrame = TlvType.Typed;

    [Fact]
    public void A_document_without_type_names_is_byte_identical()
    {
        // The optionality claim, stated as a test: adding the feature must not move a byte
        // of any document that does not use it.
        Node plain = Element("order", Element("line", Text("a")), Element("line", Text("b")));

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

        Assert.Equal(documented, TlvEncoder.Encode(plain));
        Assert.DoesNotContain(TypedFrame, Frames.TypeCodes(TlvEncoder.Encode(plain)));
    }

    [Fact]
    public void A_typed_node_round_trips()
    {
        Node tree = Element("shapes", Typed("Circle", Element("r", Text("3"))));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void A_repeated_type_name_collapses_to_a_reference()
    {
        // Two documents identical in every respect except whether the second type name
        // repeats the first. Both names are six characters, so the only difference on the
        // wire is a literal against a reference.
        Node repeated = Element("shapes", Typed("Circle", Text("3")), Typed("Circle", Text("4")));
        Node distinct = Element("shapes", Typed("Circle", Text("3")), Typed("Sphere", Text("4")));

        // Literal: marker + length + six bytes. Reference: one varint byte.
        const long literalCost = 1 + 1 + 6;
        const long referenceCost = 1;

        Assert.Equal(
            literalCost - referenceCost,
            TlvEncoder.Measure(distinct) - TlvEncoder.Measure(repeated));

        Assert.Equal(1, CountOccurrences(TlvEncoder.Encode(repeated), "Circle"u8));
    }

    [Fact]
    public void Type_names_do_not_disturb_element_or_value_ids()
    {
        // Three separate id spaces. Wrapping a subtree in a type must not shift the ids the
        // rest of the document is using, or adding a type would silently rewrite everything.
        Node withoutType = Element("root", Element("a", Text("shared")), Element("b", Text("shared")));
        Node withType = Element(
            "root",
            Typed("Wrapper", Element("a", Text("shared"))),
            Element("b", Text("shared")));

        Assert.Equal(Render(withType), Render(TlvDecoder.Decode(TlvEncoder.Encode(withType))));

        // "shared" is still written once and referenced once, exactly as without the wrapper.
        Assert.Equal(1, CountOccurrences(TlvEncoder.Encode(withType), "shared"u8));
        Assert.Equal(1, CountOccurrences(TlvEncoder.Encode(withoutType), "shared"u8));
    }

    [Fact]
    public void An_unknown_type_name_survives_a_round_trip_unchanged()
    {
        // The anti-ossification property. A reader that has never heard of this type still
        // reproduces the exact bytes, so adding a derived type does not break old readers.
        byte[] original = TlvEncoder.Encode(
            Element("feed", Typed("SomeTypeThisReaderHasNeverHeardOf", Text("payload"))));

        Node decoded = TlvDecoder.Decode(original);

        Assert.Equal(original, TlvEncoder.Encode(decoded));
    }

    [Fact]
    public void The_decoder_hands_back_the_name_as_text_and_nothing_more()
    {
        // No Type, no Activator, no assembly load — the decoder cannot be talked into
        // constructing anything, because it has no code path that constructs.
        Node decoded = TlvDecoder.Decode(TlvEncoder.Encode(Typed("System.Diagnostics.Process", Text("x"))));

        TypedNode typed = Assert.IsType<TypedNode>(decoded);
        Assert.Equal("System.Diagnostics.Process", typed.TypeName);
        Assert.IsType<TextNode>(typed.Inner);
    }

    [Fact]
    public void Type_names_can_be_refused_outright()
    {
        byte[] encoded = TlvEncoder.Encode(Element("root", Typed("Circle", Text("3"))));

        TlvFormatException error = Assert.Throws<TlvFormatException>(
            () => TlvDecoder.Decode(encoded, new TlvDecoderOptions { AllowTypeNames = false }));

        Assert.Contains("not allowed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusing_type_names_does_not_affect_documents_without_them()
    {
        Node plain = Element("root", Element("a", Text("value")));
        byte[] encoded = TlvEncoder.Encode(plain);

        Node decoded = TlvDecoder.Decode(encoded, new TlvDecoderOptions { AllowTypeNames = false });

        Assert.Equal(Render(plain), Render(decoded));
    }

    [Fact]
    public void Nesting_typed_nodes_round_trips()
    {
        Node tree = Typed("Outer", Element("box", Typed("Inner", Text("core"))));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void A_typed_node_wrapping_a_typed_node_keeps_both_names()
    {
        Node tree = Typed("Outer", Typed("Inner", Text("core")));

        TypedNode outer = Assert.IsType<TypedNode>(TlvDecoder.Decode(TlvEncoder.Encode(tree)));
        TypedNode inner = Assert.IsType<TypedNode>(outer.Inner);

        Assert.Equal("Outer", outer.TypeName);
        Assert.Equal("Inner", inner.TypeName);
    }

    [Fact]
    public void Interleaved_type_names_resolve_to_the_right_ones()
    {
        // Circle, Square, Circle, Square — a mixed-up type table still produces well-formed
        // output, so only comparing names catches it.
        Node tree = Element(
            "shapes",
            Typed("Circle", Text("1")),
            Typed("Square", Text("2")),
            Typed("Circle", Text("3")),
            Typed("Square", Text("4")));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void A_reference_to_an_undefined_type_is_rejected()
    {
        // TYPED frame with type reference 7, but no type name has been defined.
        byte[] malformed = [0x05, 0x04, 0x08, 0x04, 0x01, 0x78];

        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
    }

    [Fact]
    public void A_typed_frame_holding_more_than_one_child_is_rejected()
    {
        // TYPED wraps exactly one frame. The length below is honest — 9 bytes of value, all
        // present — so the only thing wrong with this document is the second child, and a
        // decoder that trusted the length would silently drop it.
        byte[] malformed =
        [
            0x05, 0x09,                 // TYPED, 9 bytes of value
            0x00, 0x01, (byte)'T',      // type literal "T"
            0x04, 0x01, (byte)'a',      // first child
            0x04, 0x01, (byte)'b',      // second child — one too many
        ];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("does not fill its length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_frames_count_towards_the_encoder_depth_limit()
    {
        // They are frames like any other, so they cannot be used to smuggle extra recursion
        // past the bound.
        Node deep = Text("leaf");
        for (int level = 0; level <= TlvLimits.MaxDepth; level++)
        {
            deep = Typed($"T{level}", deep);
        }

        Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(deep));
        Assert.Throws<ArgumentException>(() => TlvEncoder.Measure(deep));
    }

    [Fact]
    public void Typed_frames_count_towards_the_decoder_depth_limit()
    {
        // The encoder refuses to build one, so this is hand-rolled. Without it, a document
        // made only of nested type tags would be a way to recurse the decoder without limit.
        byte[] tooDeep = NestedTypedFrames(TlvLimits.MaxDepth + 1);

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(tooDeep));
        Assert.Contains($"Nesting deeper than {TlvLimits.MaxDepth}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_deepest_accepted_nesting_of_typed_frames_still_decodes()
    {
        // The boundary is exact rather than approximate: one frame less than the case above.
        Node decoded = TlvDecoder.Decode(NestedTypedFrames(TlvLimits.MaxDepth));

        Assert.IsType<TypedNode>(decoded);
    }

    /// <summary>
    /// <paramref name="depth"/> nested TYPED frames around a single text leaf, so the leaf
    /// sits at that depth.
    /// </summary>
    private static byte[] NestedTypedFrames(int depth)
    {
        byte[] frame = [0x04, 0x01, (byte)'x'];

        for (int level = 0; level < depth; level++)
        {
            // Type literal "T" each time: repeating the literal keeps every wrapper the same
            // shape, and the encoder is not involved, so interning does not apply.
            byte[] head = [0x00, 0x01, (byte)'T'];
            int valueLength = head.Length + frame.Length;

            using var built = new MemoryStream();
            built.WriteByte(TlvType.Typed);
            WriteVarint(built, valueLength);
            built.Write(head);
            built.Write(frame);
            frame = built.ToArray();
        }

        return frame;
    }

    private static void WriteVarint(Stream stream, int value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    [Fact]
    public void Multi_byte_type_names_round_trip()
    {
        Node tree = Typed("Кружок", Text("значение"));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
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
