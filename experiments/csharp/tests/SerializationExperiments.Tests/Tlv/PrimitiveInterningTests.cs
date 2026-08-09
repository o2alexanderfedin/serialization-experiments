using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// A primitive that recurs claims a value id through an <c>INTERN</c> wrapper and is
/// referenced thereafter, exactly as a repeated text literal is.
/// </summary>
/// <remarks>
/// <para>
/// Phase A ruled this out on the arithmetic that a reference costs three bytes and a small
/// integer costs two. That is true of small integers and false of everything wider: a repeated
/// <c>Guid</c> measured 6 bytes as interned text against 20 as a <c>GUID</c> frame, which made
/// the format's headline feature actively harmful on any document carrying a repeated
/// identifier.
/// </para>
/// <para>
/// The wrapper exists because a fixed-width value has no spare type code per primitive to
/// spend on a quiet twin the way <c>TEXT</c> has <c>TEXT_ONCE</c>.
/// </para>
/// </remarks>
public sealed class PrimitiveInterningTests
{
    private static readonly Guid Repeated = new("11112222-3333-4444-5555-666677778888");

    [Fact]
    public void Repeated_guid_is_stored_once_and_referenced_after()
    {
        Node tree = Element("root",
            Element("a", Primitives.Guid(Repeated)),
            Element("b", Primitives.Guid(Repeated)),
            Element("c", Primitives.Guid(Repeated)));

        List<byte> types = Frames.TypeCodes(TlvEncoder.Encode(tree));

        // First occurrence wrapped and claiming an id, then two references.
        Assert.Equal(1, types.Count(t => t == TlvType.Intern));
        Assert.Equal(2, types.Count(t => t == TlvType.TextRef));
        Assert.Equal(1, types.Count(t => t == TlvType.Guid));
    }

    [Fact]
    public void Repeated_guid_round_trips()
    {
        Node tree = Element("root",
            Element("a", Primitives.Guid(Repeated)),
            Element("b", Primitives.Guid(Repeated)));

        byte[] encoded = TlvEncoder.Encode(tree);
        Node decoded = TlvDecoder.Decode(encoded);

        // Compared by projection and by re-encoding rather than by node equality: an
        // ElementNode holds an IReadOnlyList, so a record's generated equality compares its
        // children by reference. PrimitiveNode compares its payload by content because it is
        // a value; a tree is a structure, and deep equality on one would be a quiet O(n) trap.
        Assert.Equal([Repeated, Repeated], Guids(decoded));
        Assert.Equal(encoded, TlvEncoder.Encode(decoded));
    }

    /// <summary>Every GUID in a decoded tree, in document order.</summary>
    private static List<Guid> Guids(Node node)
    {
        List<Guid> found = [];
        Collect(node, found);
        return found;

        static void Collect(Node current, List<Guid> found)
        {
            switch (current)
            {
                case PrimitiveNode primitive when primitive.KindOf() == PrimitiveKind.Guid:
                    found.Add(primitive.AsGuid());
                    break;

                case ElementNode element:
                    foreach (Node child in element.Children)
                    {
                        Collect(child, found);
                    }

                    break;
            }
        }
    }

    [Fact]
    public void Interned_document_re_encodes_byte_for_byte()
    {
        // The encoder rederives which values are worth interning from occurrence counts, so a
        // decoded document must encode back to the identical bytes. Without that, a document
        // that is merely relayed changes, and anything hashing or signing these bytes breaks.
        Node tree = Element("root",
            Element("a", Primitives.Guid(Repeated)),
            Element("b", Text("shared")),
            Element("c", Primitives.Guid(Repeated)),
            Element("d", Text("shared")));

        byte[] first = TlvEncoder.Encode(tree);
        byte[] second = TlvEncoder.Encode(TlvDecoder.Decode(first));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Repeated_guid_is_smaller_than_not_interning_it()
    {
        Node interned = Element("root",
            Element("a", Primitives.Guid(Repeated)),
            Element("b", Primitives.Guid(Repeated)));

        Node distinct = Element("root",
            Element("a", Primitives.Guid(Repeated)),
            Element("b", Primitives.Guid(Guid.NewGuid())));

        Assert.True(
            TlvEncoder.Encode(interned).Length < TlvEncoder.Encode(distinct).Length,
            "a repeated Guid should cost less than two distinct ones");
    }

    [Fact]
    public void Small_integer_that_repeats_is_not_interned()
    {
        // A SINT frame for 42 is two bytes; a reference is three. Interning it could only
        // lose, and would also consume an id, pushing later references toward a second
        // varint byte.
        Node tree = Element("root",
            Element("a", Primitives.Int(42)),
            Element("b", Primitives.Int(42)),
            Element("c", Primitives.Int(42)));

        List<byte> types = Frames.TypeCodes(TlvEncoder.Encode(tree));

        Assert.DoesNotContain(TlvType.Intern, types);
        Assert.Equal(3, types.Count(t => t == TlvType.SInt));
    }

    [Fact]
    public void Guid_seen_once_is_not_interned()
    {
        Node tree = Element("root", Element("a", Primitives.Guid(Repeated)));
        List<byte> types = Frames.TypeCodes(TlvEncoder.Encode(tree));

        Assert.DoesNotContain(TlvType.Intern, types);
        Assert.Contains(TlvType.Guid, types);
    }

    [Fact]
    public void Text_and_primitives_share_one_id_space_in_document_order()
    {
        // The invariant this whole change turns on. The decoder appends one entry per TEXT
        // and per INTERN frame, in the order it reads them, so the encoder must number from a
        // single counter in that same order. Two counters would agree until a document mixed
        // the kinds, and then references would resolve to the wrong value while every length
        // still checked out — the exact fault randomised round-trips caught once before.
        Node tree = Element("root",
            Element("a", Text("first")),              // claims id 0
            Element("b", Primitives.Guid(Repeated)),  // claims id 1
            Element("c", Text("second")),             // claims id 2
            Element("d", Text("first")),              // -> ref 0
            Element("e", Primitives.Guid(Repeated)),  // -> ref 1
            Element("f", Text("second")));            // -> ref 2

        byte[] encoded = TlvEncoder.Encode(tree);

        ulong[] references = Frames.Walk(encoded)
            .Where(frame => frame.Type == TlvType.TextRef)
            .Select(frame => frame.ValueId)
            .ToArray();

        Assert.Equal([0UL, 1UL, 2UL], references);

        // The references must resolve to the right values, not merely to defined ones. An
        // off-by-one id space leaves the document well formed and every length correct, so
        // only reading the values back catches it.
        Node decoded = TlvDecoder.Decode(encoded);
        Assert.Equal(["first", "second", "first", "second"], Texts(decoded));
        Assert.Equal([Repeated, Repeated], Guids(decoded));
        Assert.Equal(encoded, TlvEncoder.Encode(decoded));
    }

    /// <summary>Every text value in a decoded tree, in document order.</summary>
    private static List<string> Texts(Node node)
    {
        List<string> found = [];
        Collect(node, found);
        return found;

        static void Collect(Node current, List<string> found)
        {
            switch (current)
            {
                case TextNode text:
                    found.Add(text.Value);
                    break;

                case ElementNode element:
                    foreach (Node child in element.Children)
                    {
                        Collect(child, found);
                    }

                    break;
            }
        }
    }

    [Fact]
    public void Intern_wrapping_text_is_rejected()
    {
        // TEXT already claims an id, so allowing it inside INTERN would register the value
        // twice and put every later id out by one.
        byte[] document = [TlvType.Intern, 0x04, TlvType.Text, 0x02, (byte)'h', (byte)'i'];

        TlvFormatException error = Assert.Throws<TlvFormatException>(
            () => TlvDecoder.Decode(document));

        Assert.Contains("only a primitive may claim a value id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Intern_wrapping_an_element_is_rejected()
    {
        // ELEMENT: name literal "a", no children. A constructed frame has no single value to
        // register.
        byte[] document = [TlvType.Intern, 0x05, TlvType.Element, 0x03, 0x00, 0x01, (byte)'a'];

        TlvFormatException error = Assert.Throws<TlvFormatException>(
            () => TlvDecoder.Decode(document));

        Assert.Contains("only a primitive may claim a value id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Intern_that_does_not_fill_its_length_is_rejected()
    {
        // Declares five bytes but the inner TRUE frame is one, so a second frame would have to
        // start inside a wrapper that promised exactly one value.
        byte[] document = [TlvType.Intern, 0x05, TlvType.True, TlvType.True, TlvType.True,
                           TlvType.True, TlvType.True];

        TlvFormatException error = Assert.Throws<TlvFormatException>(
            () => TlvDecoder.Decode(document));

        Assert.Contains("does not fill its length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_to_an_undefined_primitive_id_is_rejected()
    {
        byte[] document = [TlvType.TextRef, 0x01, 0x07];

        TlvFormatException error = Assert.Throws<TlvFormatException>(
            () => TlvDecoder.Decode(document));

        Assert.Contains("is not defined", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_sharing_instances_yields_distinct_primitive_nodes()
    {
        Node tree = Element("root",
            Element("a", Primitives.Guid(Repeated)),
            Element("b", Primitives.Guid(Repeated)));

        var options = new TlvDecoderOptions { ShareValueInstances = false };
        var decoded = (ElementNode)TlvDecoder.Decode(TlvEncoder.Encode(tree), options);

        Node first = ((ElementNode)decoded.Children[0]).Children[0];
        Node second = ((ElementNode)decoded.Children[1]).Children[0];

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Payloads_are_compared_by_content_not_by_reference()
    {
        // Two equal GUIDs arrive as separate arrays. If the key hashed by reference nothing
        // would ever intern, and the feature would silently do nothing.
        Node tree = Element("root",
            Element("a", Primitives.Guid(Repeated)),
            Element("b", Primitives.Guid(new Guid(Repeated.ToByteArray()))));

        Assert.Contains(TlvType.Intern, Frames.TypeCodes(TlvEncoder.Encode(tree)));
    }

    [Fact]
    public void Same_payload_under_different_types_is_not_one_value()
    {
        // Eight bytes of zero as a double and the same eight bytes as an unknown fixed-width
        // type are different values. Keying on payload alone would conflate them.
        byte[] zeros = new byte[8];
        Node tree = Element("root",
            Element("a", new PrimitiveNode(TlvType.Float64, zeros)),
            Element("b", new UnknownNode(0x61, zeros)),
            Element("c", new PrimitiveNode(TlvType.Float64, zeros)),
            Element("d", new UnknownNode(0x61, zeros)));

        byte[] encoded = TlvEncoder.Encode(tree);

        // The double interns; the unknown frame never does, because it must go back exactly
        // as it arrived.
        Assert.Equal(1, Frames.TypeCodes(encoded).Count(t => t == TlvType.Intern));
        Assert.Equal(2, Frames.TypeCodes(encoded).Count(t => t == 0x61));
        Assert.Equal(encoded, TlvEncoder.Encode(TlvDecoder.Decode(encoded)));
    }
}
