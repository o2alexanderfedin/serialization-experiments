using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Typed values, and the shape nibble that lets a reader skip one it does not understand.
/// </summary>
public sealed class PrimitiveTypeTests
{
    [Fact]
    public void The_documented_costs_are_what_the_encoder_charges()
    {
        // The table at the top of the design document, as a test. A uniform Type-Length-Value
        // frame would add a byte to every one of these.
        Assert.Equal(1, Frame(Primitives.Bool(true)));
        Assert.Equal(1, Frame(Primitives.Null()));
        Assert.Equal(2, Frame(Primitives.Int(42)));
        Assert.Equal(2, Frame(Primitives.Int(-1)));
        Assert.Equal(5, Frame(Primitives.Float(1.5f)));
        Assert.Equal(9, Frame(Primitives.Double(3.14159265358979)));
        Assert.Equal(17, Frame(Primitives.Guid(System.Guid.NewGuid())));
    }

    [Fact]
    public void Booleans_and_null_are_a_single_byte()
    {
        Assert.Equal([0x12], TlvEncoder.Encode(Primitives.Bool(true)));
        Assert.Equal([0x11], TlvEncoder.Encode(Primitives.Bool(false)));
        Assert.Equal([0x10], TlvEncoder.Encode(Primitives.Null()));
    }

    [Fact]
    public void Small_integers_are_two_bytes_and_zigzagged()
    {
        // ZigZag: 0 -> 0, -1 -> 1, 1 -> 2, -2 -> 3.
        Assert.Equal([0x21, 0x00], TlvEncoder.Encode(Primitives.Int(0)));
        Assert.Equal([0x21, 0x01], TlvEncoder.Encode(Primitives.Int(-1)));
        Assert.Equal([0x21, 0x02], TlvEncoder.Encode(Primitives.Int(1)));
        Assert.Equal([0x21, 0x03], TlvEncoder.Encode(Primitives.Int(-2)));
    }

    [Fact]
    public void A_negative_integer_does_not_cost_ten_bytes()
    {
        // The reason SINT exists. Two's complement would set the high bit and spend the full
        // varint width on every negative number, as protobuf's int64 does.
        Assert.Equal(Frame(Primitives.Int(1)), Frame(Primitives.Int(-1)));
        Assert.Equal(Frame(Primitives.Int(1000)), Frame(Primitives.Int(-1000)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(63L)]
    [InlineData(64L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Signed_integers_round_trip(long value)
    {
        PrimitiveNode decoded = RoundTrip(Primitives.Int(value));

        Assert.Equal(value, decoded.AsInt());
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(ulong.MaxValue)]
    public void Unsigned_integers_round_trip(ulong value)
    {
        Assert.Equal(value, RoundTrip(Primitives.UInt(value)).AsUInt());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-2.25)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(double.Epsilon)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Doubles_round_trip(double value)
    {
        Assert.Equal(value, RoundTrip(Primitives.Double(value)).AsDouble());
    }

    [Fact]
    public void Negative_zero_survives()
    {
        // -0.0 and 0.0 are different values, not two spellings of one, so canonicalisation
        // must not collapse them: 1/-0.0 is negative infinity.
        double decoded = RoundTrip(Primitives.Double(-0.0)).AsDouble();

        Assert.Equal(double.NegativeInfinity, 1 / decoded);
        Assert.NotEqual(TlvEncoder.Encode(Primitives.Double(0.0)), TlvEncoder.Encode(Primitives.Double(-0.0)));
    }

    [Fact]
    public void NaN_round_trips_as_the_canonical_quiet_NaN()
    {
        Assert.True(double.IsNaN(RoundTrip(Primitives.Double(double.NaN)).AsDouble()));
        Assert.True(float.IsNaN(RoundTrip(Primitives.Float(float.NaN)).AsFloat()));

        // Every NaN encodes the same way, so a document has one encoding rather than millions.
        double other = BitConverter.UInt64BitsToDouble(0x7FF8000000000001);
        Assert.True(double.IsNaN(other));
        Assert.Equal(
            TlvEncoder.Encode(Primitives.Double(double.NaN)),
            TlvEncoder.Encode(Primitives.Double(other)));
    }

    [Fact]
    public void A_non_canonical_NaN_on_the_wire_is_rejected()
    {
        byte[] malformed = [TlvType.Float64, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x7F];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("Non-canonical", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Guids_and_blobs_round_trip()
    {
        Guid guid = Guid.NewGuid();
        Assert.Equal(guid, RoundTrip(Primitives.Guid(guid)).AsGuid());
        Assert.Equal(Guid.Empty, RoundTrip(Primitives.Guid(Guid.Empty)).AsGuid());

        byte[] blob = [1, 2, 3, 250, 251];
        Assert.Equal(blob, RoundTrip(Primitives.Bytes(blob)).AsBytes().ToArray());
        Assert.Empty(RoundTrip(Primitives.Bytes([])).AsBytes().ToArray());
    }

    [Fact]
    public void Primitives_nest_inside_elements()
    {
        Node tree = Element(
            "reading",
            Element("id", Primitives.Int(42)),
            Element("value", Primitives.Double(3.5)),
            Element("ok", Primitives.Bool(true)),
            Element("note", Primitives.Null()));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void Re_encoding_a_decoded_document_is_byte_identical()
    {
        byte[] once = TlvEncoder.Encode(Element(
            "row",
            Element("a", Primitives.Int(-70000)),
            Element("b", Primitives.Float(0.5f)),
            Element("c", Primitives.Guid(Guid.NewGuid())),
            Element("d", Primitives.Bytes([9, 8, 7]))));

        Assert.Equal(once, TlvEncoder.Encode(TlvDecoder.Decode(once)));
    }

    [Fact]
    public void Typed_values_do_not_disturb_text_interning()
    {
        // Primitives claim no value ids, so adding one must not shift the ids text is using.
        Node withPrimitive = Element(
            "root",
            Element("a", Text("shared")),
            Element("n", Primitives.Int(1)),
            Element("b", Text("shared")));

        Assert.Equal(Render(withPrimitive), Render(TlvDecoder.Decode(TlvEncoder.Encode(withPrimitive))));
        Assert.Equal(1, CountOccurrences(TlvEncoder.Encode(withPrimitive), "shared"u8));
    }

    // ---- unknown types ----

    [Theory]
    [InlineData(new byte[] { 0x1F })]                                     // empty shape
    [InlineData(new byte[] { 0x2F, 0x7F })]                               // varint shape
    [InlineData(new byte[] { 0x3F, 0xAB })]                               // 1 byte
    [InlineData(new byte[] { 0x4F, 0xAB, 0xCD })]                         // 2 bytes
    [InlineData(new byte[] { 0x5F, 1, 2, 3, 4 })]                         // 4 bytes
    [InlineData(new byte[] { 0x6F, 1, 2, 3, 4, 5, 6, 7, 8 })]             // 8 bytes
    [InlineData(new byte[] { 0x07, 0x02, 0xAA, 0xBB })]                   // length-prefixed
    [InlineData(new byte[] { 0xF0, 0x09, 0x02, 0xAA, 0xBB })]             // extension
    [InlineData(new byte[] { 0xF1, 0x09, 0x00 })]                         // private extension
    public void An_unknown_type_is_carried_through_unchanged(byte[] document)
    {
        // The point of the shape nibble: a reader that has never heard of these types still
        // knows how far each frame reaches, so a document written against a later version of
        // the format survives passing through it.
        Node decoded = TlvDecoder.Decode(document);

        Assert.IsType<UnknownNode>(decoded);
        Assert.Equal(document, TlvEncoder.Encode(decoded));
    }

    [Fact]
    public void An_unknown_type_nested_among_known_ones_survives()
    {
        byte[] document =
        [
            0x01, 0x0E,                       // ELEMENT, 14 bytes of value
            0x00, 0x01, (byte)'r',            // name "r"
            0x21, 0x2A,                       // SINT 21
            0x6F, 1, 2, 3, 4, 5, 6, 7, 8,     // unknown 8-byte type
        ];

        Node decoded = TlvDecoder.Decode(document);

        Assert.Equal(document, TlvEncoder.Encode(decoded));
    }

    [Fact]
    public void Unknown_types_can_be_refused()
    {
        byte[] document = [0x6F, 1, 2, 3, 4, 5, 6, 7, 8];

        TlvFormatException error = Assert.Throws<TlvFormatException>(
            () => TlvDecoder.Decode(document, new TlvDecoderOptions { AllowUnknownTypes = false }));

        Assert.Contains("not known", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusing_unknown_types_does_not_affect_documents_without_them()
    {
        Node tree = Element("root", Element("a", Primitives.Int(7)));
        byte[] encoded = TlvEncoder.Encode(tree);

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(encoded, new TlvDecoderOptions { AllowUnknownTypes = false })));
    }

    // ---- rejection ----

    [Theory]
    [InlineData((byte)0xB0)]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xD0)]
    [InlineData((byte)0xE0)]
    public void A_reserved_shape_is_rejected_because_it_cannot_be_skipped(byte type)
    {
        // The one unknown that must be an error: with no width, a reader cannot step over it,
        // and guessing would desynchronise everything after it.
        TlvFormatException error = Assert.Throws<TlvFormatException>(
            () => TlvDecoder.Decode([type, 0x00, 0x00]));

        Assert.Contains("reserved shape", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_zero_is_rejected()
    {
        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([0x00, 0x00]));

        Assert.Contains("reserved", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fixed_payload_cut_short_by_the_buffer_is_rejected()
    {
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([TlvType.Float64, 1, 2, 3]));
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([TlvType.Guid, 1, 2, 3, 4]));
    }

    [Fact]
    public void A_fixed_payload_cut_short_by_its_parent_is_rejected()
    {
        // Bytes remain in the buffer, so a bounds check against the buffer alone passes. The
        // eight-byte double overruns the element that declares only five bytes of value.
        byte[] malformed =
        [
            0x01, 0x05,                       // ELEMENT declaring 5 bytes of value
            0x00, 0x01, (byte)'a',            // name "a" fills all five
            0x62, 1, 2, 3, 4, 5, 6, 7, 8,     // ...then a double the element has no room for
        ];

        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
    }

    [Fact]
    public void An_extension_declaring_more_than_the_document_holds_is_rejected()
    {
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode([0xF0, 0x01, 0x40, 0xAA]));
    }

    // ---- backward compatibility ----

    [Fact]
    public void The_documented_example_is_unchanged_by_any_of_this()
    {
        // Every pre-existing type code is length-prefixed and so already sits in shape 0.
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

    [Fact]
    public void Booleans_and_null_read_back_through_the_accessors()
    {
        Assert.True(RoundTrip(Primitives.Bool(true)).AsBool());
        Assert.False(RoundTrip(Primitives.Bool(false)).AsBool());
        Assert.True(RoundTrip(Primitives.Null()).IsNull());
        Assert.False(RoundTrip(Primitives.Bool(false)).IsNull());
    }

    [Fact]
    public void Every_kind_reports_itself()
    {
        Assert.Equal(PrimitiveKind.Null, Primitives.Null().KindOf());
        Assert.Equal(PrimitiveKind.Boolean, Primitives.Bool(true).KindOf());
        Assert.Equal(PrimitiveKind.Boolean, Primitives.Bool(false).KindOf());
        Assert.Equal(PrimitiveKind.SignedInteger, Primitives.Int(1).KindOf());
        Assert.Equal(PrimitiveKind.UnsignedInteger, Primitives.UInt(1).KindOf());
        Assert.Equal(PrimitiveKind.Single, Primitives.Float(1).KindOf());
        Assert.Equal(PrimitiveKind.Double, Primitives.Double(1).KindOf());
        Assert.Equal(PrimitiveKind.Guid, Primitives.Guid(System.Guid.Empty).KindOf());
        Assert.Equal(PrimitiveKind.Bytes, Primitives.Bytes([1]).KindOf());
    }

    [Fact]
    public void Reading_a_value_as_the_wrong_type_is_refused()
    {
        // The accessors are the one place a caller can be wrong about what it holds, so they
        // say so rather than reinterpreting the bytes — a double read as an integer would
        // otherwise return whatever its first varint byte happened to spell.
        PrimitiveNode number = Primitives.Double(1.5);

        Assert.Throws<InvalidOperationException>(() => number.AsInt());
        Assert.Throws<InvalidOperationException>(() => number.AsUInt());
        Assert.Throws<InvalidOperationException>(() => number.AsBool());
        Assert.Throws<InvalidOperationException>(() => number.AsFloat());
        Assert.Throws<InvalidOperationException>(() => number.AsGuid());
        Assert.Throws<InvalidOperationException>(() => Primitives.Int(1).AsDouble());
        Assert.Throws<InvalidOperationException>(() => Primitives.Int(1).AsBytes().ToArray());
    }

    [Fact]
    public void A_non_canonical_binary32_NaN_on_the_wire_is_rejected()
    {
        // The binary64 case has its own test; this is the narrower width, where the quiet bit
        // sits in a different place and an off-by-one mask would slip through.
        byte[] malformed = [TlvType.Float32, 0x01, 0x00, 0xC0, 0x7F];

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(malformed));
        Assert.Contains("Non-canonical binary32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_known_value_type_can_be_classified()
    {
        // TlvType.IsKnown and Primitives.KindOf each list the types they recognise, in
        // different files. Adding a type to one and not the other would produce a value the
        // decoder happily builds and no caller can read — so the two lists are checked
        // against each other rather than trusted to stay in step.
        byte[] structural =
        [
            TlvType.Element, TlvType.Text, TlvType.TextRef, TlvType.TextOnce, TlvType.Typed,
        ];

        for (int candidate = 0; candidate <= byte.MaxValue; candidate++)
        {
            byte type = (byte)candidate;
            if (!TlvType.IsKnown(type) || structural.Contains(type))
            {
                continue;
            }

            PrimitiveKind kind = new PrimitiveNode(type, ReadOnlyMemory<byte>.Empty).KindOf();
            Assert.True(Enum.IsDefined(kind), $"type 0x{type:X2} classified as {kind}");
        }
    }

    [Fact]
    public void Every_shape_reports_a_width_consistent_with_its_nibble()
    {
        // The skip rule a reader depends on, checked directly rather than inferred from the
        // frames that happen to be tested elsewhere.
        (byte Type, int Width)[] expected =
        [
            (0x30, 1), (0x40, 2), (0x50, 4), (0x60, 8), (0x70, 16), (0x80, 32), (0x90, 64), (0xA0, 128),
        ];

        foreach ((byte type, int width) in expected)
        {
            Assert.Equal(PayloadShape.Fixed, TlvType.ShapeOf(type));
            Assert.Equal(width, TlvType.FixedWidthOf(type));
        }

        Assert.Equal(PayloadShape.LengthPrefixed, TlvType.ShapeOf(0x00));
        Assert.Equal(PayloadShape.Empty, TlvType.ShapeOf(0x10));
        Assert.Equal(PayloadShape.Varint, TlvType.ShapeOf(0x20));
        Assert.Equal(PayloadShape.Extension, TlvType.ShapeOf(0xF0));

        foreach (byte reserved in (byte[])[0xB0, 0xC0, 0xD0, 0xE0])
        {
            Assert.Equal(PayloadShape.Reserved, TlvType.ShapeOf(reserved));
        }
    }

    private static int Frame(Node node) => TlvEncoder.Encode(node).Length;

    private static PrimitiveNode RoundTrip(PrimitiveNode node)
    {
        byte[] encoded = TlvEncoder.Encode(node);
        var decoded = Assert.IsType<PrimitiveNode>(TlvDecoder.Decode(encoded));

        // Canonical: a decoded value must re-encode to the bytes it came from.
        Assert.Equal(encoded, TlvEncoder.Encode(decoded));
        return decoded;
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
