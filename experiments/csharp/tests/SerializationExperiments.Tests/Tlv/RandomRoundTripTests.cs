using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Randomised round-trips over trees mixing every node kind.
/// </summary>
/// <remarks>
/// The hand-written tests each pin one rule. These exist to catch the interactions nobody
/// thought to write down — a value id and a type id colliding, an interning decision that
/// depends on a node kind two levels up, a length that is right for one shape and wrong for
/// a mix. Seeds are fixed, so a failure is reproducible rather than a story about a build
/// that once went red.
/// </remarks>
public sealed class RandomRoundTripTests
{
    private static readonly string[] Names = ["a", "bb", "item", "order", "x", "very-long-element-name"];

    private static readonly string[] Values =
        ["", "x", "ab", "shared", "repeated-value", "日本語", "🙂", new string('p', 300)];

    private static readonly string[] TypeNames = ["Circle", "Square", "T", "Namespace.Deeply.Nested.Type"];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    [InlineData(55)]
    [InlineData(89)]
    public void Random_trees_round_trip(int seed)
    {
        var random = new Random(seed);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            Node tree = Build(random, depth: 0);

            byte[] encoded = TlvEncoder.Encode(tree);
            Node decoded = TlvDecoder.Decode(encoded);

            Assert.Equal(Render(tree), Render(decoded));

            // Canonical: decoding and re-encoding must reproduce the bytes exactly, or the
            // encoder's choices depend on something the document does not carry.
            Assert.Equal(encoded, TlvEncoder.Encode(decoded));

            // Measured length and written length must agree for every shape, not just the
            // ones with hand-written tests.
            Assert.Equal(TlvEncoder.Measure(tree), encoded.Length);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_trees_encode_the_same_to_a_stream_as_to_an_array(int seed)
    {
        var random = new Random(seed);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            Node tree = Build(random, depth: 0);

            using var stream = new MemoryStream();
            long written = TlvEncoder.Encode(tree, new StreamSink(stream));
            byte[] array = TlvEncoder.Encode(tree);

            Assert.Equal(array.Length, written);
            Assert.Equal(array, stream.ToArray());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Truncating_a_document_anywhere_is_rejected_not_misread(int seed)
    {
        // Every proper prefix of a valid document is invalid. A decoder that accepted one
        // would be reading past a length it never verified.
        var random = new Random(seed);

        for (int iteration = 0; iteration < 20; iteration++)
        {
            byte[] encoded = TlvEncoder.Encode(Build(random, depth: 0));

            for (int cut = 1; cut < encoded.Length; cut++)
            {
                byte[] truncated = encoded[..cut];

                try
                {
                    TlvDecoder.Decode(truncated);
                }
                catch (TlvFormatException)
                {
                    continue;
                }

                Assert.Fail($"a {cut}-byte prefix of a {encoded.Length}-byte document decoded without error");
            }
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(11)]
    public void Corrupting_a_single_byte_never_crashes_the_decoder(int seed)
    {
        // A hostile document may be rejected or may happen to remain valid, but it must not
        // throw anything other than TlvFormatException — no index-out-of-range, no overflow.
        var random = new Random(seed);

        for (int iteration = 0; iteration < 20; iteration++)
        {
            byte[] encoded = TlvEncoder.Encode(Build(random, depth: 0));

            for (int index = 0; index < encoded.Length; index++)
            {
                byte[] corrupted = (byte[])encoded.Clone();
                corrupted[index] ^= 0xFF;

                try
                {
                    TlvDecoder.Decode(corrupted);
                }
                catch (TlvFormatException)
                {
                    // The expected way to fail.
                }
                catch (Exception error)
                {
                    Assert.Fail(
                        $"flipping byte {index} of a {encoded.Length}-byte document threw " +
                        $"{error.GetType().Name}: {error.Message}");
                }
            }
        }
    }

    private static Node Build(Random random, int depth)
    {
        // Shallow enough to stay well inside the depth limit, wide enough to force repeats.
        int choice = random.Next(depth >= 6 ? 1 : 10);

        return choice switch
        {
            0 or 1 or 2 => new TextNode(Values[random.Next(Values.Length)]),
            3 => new TypedNode(TypeNames[random.Next(TypeNames.Length)], Build(random, depth + 1)),
            _ => BuildElement(random, depth),
        };
    }

    private static Node BuildElement(Random random, int depth)
    {
        int childCount = random.Next(4);
        Node[] children = new Node[childCount];
        for (int index = 0; index < childCount; index++)
        {
            children[index] = Build(random, depth + 1);
        }

        return new ElementNode(Names[random.Next(Names.Length)], children);
    }
}
