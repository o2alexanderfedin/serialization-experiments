using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// The encoder and decoder bound nesting at the same depth, so anything that encodes decodes.
/// </summary>
/// <remarks>
/// The benchmarks surfaced the opposite: the decoder capped nesting to bound stack use while
/// the encoder had no limit, so the encoder produced documents its own decoder refused —
/// visible as an NA in the depth-1000 benchmark row where every other case reported a time.
/// Round-trip coverage used depth 300 and never reached it. These tests now sit either side
/// of the shared limit rather than recording the gap.
/// </remarks>
public sealed class DepthLimitTests
{
    /// <summary>Deepest chain that fits: the leaf text sits at exactly the limit.</summary>
    private const int DeepestAccepted = TlvLimits.MaxDepth;

    [Fact]
    public void The_deepest_accepted_document_round_trips()
    {
        Node atLimit = Chain(DeepestAccepted);

        Assert.Equal(Render(atLimit), Render(TlvDecoder.Decode(TlvEncoder.Encode(atLimit))));
    }

    [Fact]
    public void One_frame_deeper_is_refused_by_the_encoder()
    {
        // The boundary is exact: this differs from the accepted case by a single element.
        ArgumentException error =
            Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(Chain(DeepestAccepted + 1)));

        Assert.Contains($"deeper than {TlvLimits.MaxDepth}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pathologically_deep_tree_is_refused_without_exhausting_the_stack()
    {
        // One frame past the limit does not distinguish a guard on every recursive pass from
        // a guard on only one of them, because whichever runs first reports the failure. This
        // depth does: any pass that recurses unguarded reaches a StackOverflowException, which
        // cannot be caught and takes the test host down rather than failing an assertion.
        Node absurd = Chain(100_000);

        Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(absurd));
        Assert.Throws<ArgumentException>(() => TlvEncoder.Measure(absurd));
        Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(absurd, new CountingSink()));
    }

    [Fact]
    public void Measure_refuses_it_too()
    {
        // Measure is public, and callers size buffers from it before encoding.
        Assert.Throws<ArgumentException>(() => TlvEncoder.Measure(Chain(DeepestAccepted + 1)));
    }

    [Fact]
    public void Nothing_is_emitted_before_the_encoder_gives_up()
    {
        // The check lives in the measuring pass, so a rejected tree leaves the sink untouched
        // rather than half-written.
        var sink = new CountingSink();

        Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(Chain(DeepestAccepted + 1), sink));
        Assert.Equal(0, sink.BytesWritten);
    }

    [Fact]
    public void A_too_deep_document_from_elsewhere_is_still_refused_by_the_decoder()
    {
        // The encoder can no longer produce one, so it has to be assembled by hand: nesting
        // one frame past the limit, each element holding the next.
        byte[] tooDeep = HandRolledChain(TlvLimits.MaxDepth + 1);

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(tooDeep));

        Assert.Contains($"Nesting deeper than {TlvLimits.MaxDepth}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_encoder_and_decoder_agree_on_the_boundary()
    {
        // Whatever the limit is set to, the two sides must draw the line in the same place.
        byte[] deepest = TlvEncoder.Encode(Chain(DeepestAccepted));

        Assert.NotNull(TlvDecoder.Decode(deepest));
        Assert.Throws<ArgumentException>(() => TlvEncoder.Encode(Chain(DeepestAccepted + 1)));
        Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(HandRolledChain(TlvLimits.MaxDepth + 1)));
    }

    private static Node Chain(int depth)
    {
        Node node = Text("leaf");
        for (int level = depth; level > 0; level--)
        {
            node = Element($"level{level}", node);
        }

        return node;
    }

    /// <summary>
    /// Builds nested ELEMENT frames directly, bypassing the encoder's depth check.
    /// </summary>
    /// <param name="depth">Frames beneath the root, so the innermost sits at this depth.</param>
    private static byte[] HandRolledChain(int depth)
    {
        // Innermost frame: ELEMENT, length 3, name literal (marker 0, length 1, "a").
        byte[] frame = [0x01, 0x03, 0x00, 0x01, (byte)'a'];

        for (int level = 0; level < depth; level++)
        {
            // Each wrapper repeats the name literal rather than referencing it, so every
            // frame has the same shape and the length stays a single varint byte for a while.
            byte[] head = [0x00, 0x01, (byte)'a'];
            int valueLength = head.Length + frame.Length;

            using var built = new MemoryStream();
            built.WriteByte(0x01);
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
}
