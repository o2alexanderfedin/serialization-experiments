using BenchmarkDotNet.Attributes;
using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Cost of encoding, split so the measuring pass can be seen on its own.
/// </summary>
/// <remarks>
/// <see cref="MeasureOnly"/> against <see cref="EncodeToCounter"/> is the price of the
/// two-pass design: measuring walks the tree without producing bytes, and the full encode
/// walks it twice. <see cref="EncodeToCounter"/> against <see cref="EncodeToArray"/>
/// isolates what the output buffer itself costs.
/// </remarks>
[MemoryDiagnoser]
public class EncodeBenchmarks
{
    private Node tree = null!;

    [Params("repeated", "unique", "deep", "text-heavy", "values-repeat", "values-unique", "values-mixed", "typed")]
    public string Shape { get; set; } = "repeated";

    [Params(100, 1_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup() => this.tree = Documents.Build(this.Shape, this.Size);

    /// <summary>Pass 1 alone: walks the tree, produces no bytes.</summary>
    [Benchmark]
    public long MeasureOnly() => TlvEncoder.Measure(this.tree);

    /// <summary>Both passes, with the output discarded — no buffer, no copy.</summary>
    [Benchmark(Baseline = true)]
    public long EncodeToCounter() => TlvEncoder.Encode(this.tree, new CountingSink());

    /// <summary>Both passes into a real buffer, which is what a caller normally pays.</summary>
    [Benchmark]
    public int EncodeToArray() => TlvEncoder.Encode(this.tree).Length;
}
