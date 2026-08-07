using BenchmarkDotNet.Attributes;
using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Cost of decoding, for comparison against the encode side.
/// </summary>
/// <remarks>
/// Decoding is single-pass — lengths are already on the wire — so this is roughly the floor
/// the two-pass encoder is measured against.
/// </remarks>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    private byte[] encoded = [];

    [Params("repeated", "unique", "deep", "text-heavy")]
    public string Shape { get; set; } = "repeated";

    [Params(100, 1_000)]
    public int Size { get; set; }

    /// <summary>Encoded size, reported so throughput can be read per byte rather than per node.</summary>
    public int EncodedBytes => this.encoded.Length;

    [GlobalSetup]
    public void Setup()
    {
        Node tree = this.Shape switch
        {
            "repeated" => Documents.RepeatedNames(this.Size),
            "unique" => Documents.UniqueNames(this.Size),
            "deep" => Documents.Deep(this.Size),
            "text-heavy" => Documents.TextHeavy(this.Size, textLength: 200),
            _ => throw new ArgumentOutOfRangeException(nameof(this.Shape)),
        };

        this.encoded = TlvEncoder.Encode(tree);
    }

    [Benchmark]
    public Node Decode() => TlvDecoder.Decode(this.encoded);
}
