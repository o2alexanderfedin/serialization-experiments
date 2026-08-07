using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Pins the encoder/decoder depth asymmetry surfaced by the benchmarks.
/// </summary>
/// <remarks>
/// The decoder caps nesting at 512 frames to bound stack use, but the encoder has no
/// corresponding limit. It will therefore happily produce a document its own decoder
/// refuses. Existing round-trip coverage used depth 300 and never noticed; the depth-1000
/// benchmark did, reporting NA where every other case reported a time.
/// </remarks>
public sealed class DepthLimitTests
{
    private const int BeyondDecoderLimit = 1_000;

    [Fact]
    public void Encoder_accepts_a_document_deeper_than_the_decoder_allows()
    {
        Node tooDeep = Chain(BeyondDecoderLimit);

        byte[] encoded = TlvEncoder.Encode(tooDeep);

        Assert.Equal(TlvEncoder.Measure(tooDeep), encoded.Length);
    }

    [Fact]
    public void Decoder_rejects_that_same_document()
    {
        byte[] encoded = TlvEncoder.Encode(Chain(BeyondDecoderLimit));

        TlvFormatException error = Assert.Throws<TlvFormatException>(() => TlvDecoder.Decode(encoded));

        Assert.Contains("Nesting deeper than 512", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trip_still_works_just_under_the_limit()
    {
        Node atLimit = Chain(510);

        Assert.Equal(Render(atLimit), Render(TlvDecoder.Decode(TlvEncoder.Encode(atLimit))));
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
}
