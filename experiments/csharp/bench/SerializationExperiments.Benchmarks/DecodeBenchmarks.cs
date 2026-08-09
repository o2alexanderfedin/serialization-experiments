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

    [Params("repeated", "unique", "deep", "text-heavy", "values-repeat", "values-unique", "values-mixed", "typed", "records", "records-text")]
    public string Shape { get; set; } = "repeated";

    [Params(100, 1_000)]
    public int Size { get; set; }

    /// <summary>Encoded size, reported so throughput can be read per byte rather than per node.</summary>
    public int EncodedBytes => this.encoded.Length;

    [GlobalSetup]
    public void Setup()
    {
        Node tree = Documents.Build(this.Shape, this.Size);

        this.encoded = TlvEncoder.Encode(tree);
    }

    [Benchmark]
    public Node Decode() => TlvDecoder.Decode(this.encoded);
}
