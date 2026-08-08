using System.Globalization;
using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Reports bytes allocated per encode, exactly, for one operation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EncodeBenchmarks"/> reports allocation too, but it divides a total by an
/// auto-scaled operation count, so one-time costs are amortised differently from run to run.
/// That was enough to move <c>MeasureOnly</c> — code that had not changed — by 95% between
/// two runs, which is far too noisy to attribute a 20% change to an edit.
/// </para>
/// <para>
/// <see cref="System.GC.GetAllocatedBytesForCurrentThread"/> reads the calling thread's
/// allocation counter, so a single warmed-up operation gives an exact figure with nothing to
/// amortise. Timing still belongs in BenchmarkDotNet; this answers only "how many bytes".
/// </para>
/// </remarks>
internal static class Allocations
{
    /// <summary>Operations run before measuring, to settle JIT, statics, and the array pool.</summary>
    private const int WarmupRuns = 5;

    internal static void Report()
    {
        Console.WriteLine("| Shape | Size | Output | Measure | Encode→counter | Encode→array | Array overhead |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (string shape in Documents.Shapes)
        {
            foreach (int count in new[] { 100, 1_000 })
            {
                Node tree = Documents.Build(shape, count);
                int output = TlvEncoder.Encode(tree).Length;

                long measure = AllocatedBy(() => TlvEncoder.Measure(tree));
                long counter = AllocatedBy(() => TlvEncoder.Encode(tree, new CountingSink()));
                long array = AllocatedBy(() => TlvEncoder.Encode(tree));

                // What producing the array costs beyond producing the bytes: 1.00x means one
                // allocation of exactly the output size and nothing else.
                double overhead = (double)(array - counter) / output;

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {shape} | {count} | {output:N0} | {measure:N0} | {counter:N0} | {array:N0} | {overhead:N2}x |"));
            }
        }
    }

    /// <summary>Bytes allocated by one run of <paramref name="action"/>, after warmup.</summary>
    private static long AllocatedBy(Action action)
    {
        for (int run = 0; run < WarmupRuns; run++)
        {
            action();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
