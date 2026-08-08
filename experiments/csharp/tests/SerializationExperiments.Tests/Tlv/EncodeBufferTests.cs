using System.Text;
using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// The measuring pass knows the exact output length, so the emit pass never guesses at
/// buffer sizes — neither for the document as a whole nor for an individual UTF-8 literal.
/// </summary>
/// <remarks>
/// These pin behaviour that byte-for-byte comparison cannot see. A document encodes to the
/// same bytes whether the buffer was pre-sized or grown, and whether a literal went through
/// the stack or the array pool — so the round-trip tests stay green either way. What the
/// checks below catch is the length arithmetic drifting from the bytes actually produced,
/// which is exactly what the stack/pool split makes possible.
/// </remarks>
public sealed class EncodeBufferTests
{
    /// <summary>Just past <c>MaxStackUtf8</c>, so the pooled path is taken.</summary>
    private const int PooledLength = 257;

    [Fact]
    public void Encoded_array_is_exactly_the_measured_length()
    {
        // A growable buffer hands back an array with slack, or pays a copy to trim it.
        Node tree = Element("root", Element("a", Text("alpha")), Element("b", Text("beta")));

        Assert.Equal(TlvEncoder.Measure(tree), TlvEncoder.Encode(tree).Length);
    }

    [Theory]
    [InlineData("repeated")]
    [InlineData("unique")]
    public void Measure_agrees_with_the_array_for_larger_documents(string shape)
    {
        Node tree = Fan(200, distinctNames: shape == "unique");

        Assert.Equal(TlvEncoder.Measure(tree), TlvEncoder.Encode(tree).Length);
    }

    [Fact]
    public void A_value_longer_than_the_stack_buffer_round_trips()
    {
        Node tree = Element("root", Element("body", Text(new string('x', PooledLength))));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void An_element_name_longer_than_the_stack_buffer_round_trips()
    {
        Node tree = Element("root", Element(new string('n', PooledLength), Text("v")));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Theory]
    [InlineData("é")]                        // 2 bytes per char
    [InlineData("日本語")]                    // 3 bytes per char
    [InlineData("🙂")]                        // surrogate pair, 4 bytes
    [InlineData("aé日🙂")]                    // mixed widths
    public void Multi_byte_text_round_trips(string value)
    {
        // Byte length and char length differ here, so a buffer sized from the wrong one
        // truncates the literal or overruns the frame.
        Node tree = Element("root", Element("a", Text(value)));
        byte[] encoded = TlvEncoder.Encode(tree);

        // The literal must appear whole: a buffer sized from the char count would cut it short.
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        Assert.True(
            encoded.AsSpan().IndexOf(utf8) >= 0,
            $"The {utf8.Length}-byte UTF-8 form of \"{value}\" is not present in the output.");
        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(encoded)));
    }

    [Fact]
    public void Multi_byte_element_names_round_trip()
    {
        Node tree = Element("выгрузка", Element("日本語", Text("🙂")));

        Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
    }

    [Fact]
    public void Multi_byte_text_straddling_the_stack_boundary_round_trips()
    {
        // Repeats of a 4-byte character land the byte count either side of the stack limit
        // while the char count stays well under it.
        foreach (int repeats in new[] { 63, 64, 65, 100 })
        {
            Node tree = Element("root", Element("a", Text(string.Concat(Enumerable.Repeat("🙂", repeats)))));

            Assert.Equal(Render(tree), Render(TlvDecoder.Decode(TlvEncoder.Encode(tree))));
        }
    }

    [Fact]
    public void Writing_past_the_measured_length_throws_rather_than_growing()
    {
        // A silently-growing sink would let the two passes diverge without complaint.
        var sink = new BufferSink(new byte[4]);
        sink.Write([1, 2, 3]);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => sink.Write([4, 5]));

        Assert.Contains("overran", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, sink.BytesWritten);
    }

    [Fact]
    public void A_buffer_filled_exactly_to_its_length_is_accepted()
    {
        var sink = new BufferSink(new byte[3]);
        sink.Write([1, 2]);
        sink.Write([3]);

        Assert.Equal(3, sink.BytesWritten);
    }

    [Fact]
    public void Encoding_to_a_stream_and_to_an_array_produces_the_same_bytes()
    {
        // The two public entry points now take different paths to the same bytes.
        Node tree = Fan(50, distinctNames: true);

        using var stream = new MemoryStream();
        long written = TlvEncoder.Encode(tree, new StreamSink(stream));
        byte[] array = TlvEncoder.Encode(tree);

        Assert.Equal(array.Length, written);
        Assert.Equal(array, stream.ToArray());
    }

    private static Node Fan(int count, bool distinctNames)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = Element(
                distinctNames ? $"field{index}" : "line",
                Text($"value{index}"));
        }

        return new ElementNode("root", children);
    }
}
