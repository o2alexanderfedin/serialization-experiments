using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Round-trip is the primary correctness check.
/// </summary>
/// <remarks>
/// A name table desync produces a document whose lengths are all self-consistent and whose
/// structure validates cleanly — only the names resolve to the wrong entries. Length
/// arithmetic and the measured-equals-written assertion both pass on such a document. Only
/// decoding back to the source catches it.
/// </remarks>
public sealed class TlvRoundTripTests
{
    public static TheoryData<string, Node> Documents() => new()
    {
        { "documented example", Element("order", Element("line", Text("a")), Element("line", Text("b"))) },
        { "single empty element", Element("empty") },
        { "element with empty text", Element("blank", Text(string.Empty)) },
        { "text only child", Element("wrapper", Text("hello world")) },
        { "siblings with distinct names", Element("root", Element("a", Text("1")), Element("b", Text("2"))) },
        { "recursive same name", Element("a", Element("a", Element("a", Text("x")))) },
        { "mixed content", Element("p", Text("before"), Element("em", Text("mid")), Text("after")) },
        { "repeat after gap", Element("r", Element("x", Text("1")), Element("y", Text("2")), Element("x", Text("3"))) },
        { "unicode name and text", Element("naïve—ünïcode", Text("日本語 🎉")) },
        { "many distinct names", ManyNames(60) },
        { "deep nesting", DeepChain(300) },
        { "wide fan out", WideFanOut(200) },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Decodes_back_to_the_original_document(string description, Node original)
    {
        byte[] encoded = TlvEncoder.Encode(original);

        Node decoded = TlvDecoder.Decode(encoded);

        Assert.Equal(Render(original), Render(decoded));
        Assert.Equal(TlvEncoder.Measure(original), encoded.Length);
        Assert.False(string.IsNullOrEmpty(description));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Re_encoding_a_decoded_document_is_byte_identical(string description, Node original)
    {
        byte[] once = TlvEncoder.Encode(original);

        byte[] twice = TlvEncoder.Encode(TlvDecoder.Decode(once));

        Assert.Equal(once, twice);
        Assert.False(string.IsNullOrEmpty(description));
    }

    [Fact]
    public void Reconstructs_a_tag_name_the_decoder_has_never_seen()
    {
        // The whole point of the dynamic table: nothing is registered in advance, so a name
        // invented by the sender still round-trips character for character.
        Node original = Element("flurbleWidget", Element("zzTop9000", Text("payload")));

        Node decoded = TlvDecoder.Decode(TlvEncoder.Encode(original));

        Assert.Equal("flurbleWidget", Assert.IsType<ElementNode>(decoded).Name);
        Assert.Equal(Render(original), Render(decoded));
    }

    [Fact]
    public void Ids_follow_document_order_not_completion_order()
    {
        // The parent claims its id before descending. If ids were assigned as subtrees
        // completed, "line" would take id 0 and "order" id 1, the decoder would resolve the
        // second reference to <order>, and only this comparison would notice.
        Node original = Element("order", Element("line", Text("a")), Element("line", Text("b")));
        byte[] encoded = TlvEncoder.Encode(original);

        Assert.Equal(0x02, encoded[^4]);  // NameRef 2 -> id 1 -> "line"
        Assert.Equal("<order><line>a</line><line>b</line></order>", Render(TlvDecoder.Decode(encoded)));
    }

    private static Node ManyNames(int count)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = Element($"tag{index}", Text(index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new ElementNode("root", children);
    }

    private static Node DeepChain(int depth)
    {
        Node node = Text("leaf");
        for (int level = depth; level > 0; level--)
        {
            node = Element($"level{level}", node);
        }

        return node;
    }

    private static Node WideFanOut(int count)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = Element("item", Text($"value{index}"));
        }

        return new ElementNode("list", children);
    }
}
