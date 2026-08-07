using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

public sealed class TlvEncoderTests
{
    /// <summary>The worked example from docs/xml-to-tlv-dynamic-tag-table.md.</summary>
    private static readonly ElementNode DocumentedTree =
        Element("order", Element("line", Text("a")), Element("line", Text("b")));

    private const string DocumentedXml = "<order><line>a</line><line>b</line></order>";

    private static readonly byte[] DocumentedBytes =
    [
        0x01, 0x18,                                     // ELEMENT, value length 24
        0x00, 0x05, 0x6F, 0x72, 0x64, 0x65, 0x72,       // literal "order" -> id 0
        0x01, 0x09,                                     // ELEMENT, value length 9
        0x00, 0x04, 0x6C, 0x69, 0x6E, 0x65,             // literal "line"  -> id 1
        0x02, 0x01, 0x61,                               // TEXT "a"
        0x01, 0x04,                                     // ELEMENT, value length 4
        0x02,                                           // NameRef 2 -> id 1 ("line")
        0x02, 0x01, 0x62,                               // TEXT "b"
    ];

    [Fact]
    public void Produces_the_documented_byte_sequence()
    {
        Assert.Equal(DocumentedBytes, TlvEncoder.Encode(DocumentedTree));
    }

    [Fact]
    public void Encodes_the_documented_example_smaller_than_its_xml()
    {
        Assert.Equal(43, DocumentedXml.Length);
        Assert.Equal(26, TlvEncoder.Encode(DocumentedTree).Length);
    }

    [Fact]
    public void Repeating_a_name_costs_less_the_second_time()
    {
        // Identical subtree shape, different encoded size: the first <line> carries the
        // literal, the second is a one-byte reference. This is exactly why the measuring
        // pass must carry the name table instead of being pure arithmetic over the tree.
        long oneChild = TlvEncoder.Measure(Element("root", Element("line", Text("a"))));
        long twoChildren = TlvEncoder.Measure(
            Element("root", Element("line", Text("a")), Element("line", Text("b"))));

        Assert.Equal(11, TlvEncoder.Measure(Element("line", Text("a"))));  // literal: 11 bytes
        Assert.Equal(6, twoChildren - oneChild);                           // reference: 6 bytes
    }

    [Fact]
    public void Measure_agrees_with_the_bytes_actually_written()
    {
        Node tree = Element(
            "catalogue",
            Element("item", Text("first")),
            Element("item", Element("nested", Text("deep"))),
            Element("other", Text("x")));

        var counter = new CountingSink();
        TlvEncoder.Encode(tree, counter);

        Assert.Equal(TlvEncoder.Measure(tree), counter.BytesWritten);
        Assert.Equal(TlvEncoder.Encode(tree).Length, counter.BytesWritten);
    }

    [Fact]
    public void Counting_sink_allocates_nothing_and_matches_the_real_encoding()
    {
        Node tree = DeepChain(200);

        var counter = new CountingSink();
        TlvEncoder.Encode(tree, counter);

        Assert.Equal(TlvEncoder.Encode(tree).Length, counter.BytesWritten);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(300)]
    public void Handles_value_lengths_that_cross_varint_boundaries(int textLength)
    {
        Node tree = Element("payload", Text(new string('x', textLength)));

        byte[] encoded = TlvEncoder.Encode(tree);

        Assert.Equal(TlvEncoder.Measure(tree), encoded.Length);
        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(encoded)));
    }

    [Fact]
    public void Rejects_a_null_tree()
    {
        Assert.Throws<ArgumentNullException>(() => TlvEncoder.Encode(null!));
        Assert.Throws<ArgumentNullException>(() => TlvEncoder.Measure(null!));
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
}
