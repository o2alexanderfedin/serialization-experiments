using System.Reflection;
using BenchmarkDotNet.Running;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Benchmark entry point.
/// </summary>
/// <remarks>
/// Must be run in Release; BenchmarkDotNet refuses a Debug build.
/// <code>
/// dotnet run -c Release --project bench/SerializationExperiments.Benchmarks -- --filter '*'
/// dotnet run -c Release --project bench/SerializationExperiments.Benchmarks -- --filter '*Encode*' --job short
/// </code>
/// </remarks>
public static class Program
{
    /// <summary>Dispatches to BenchmarkDotNet's switcher, which parses the arguments.</summary>
    /// <param name="args">
    /// BenchmarkDotNet arguments, e.g. <c>--filter</c> and <c>--job</c>. Two exceptions print
    /// a report and exit, because both are exact rather than sampled and a timing harness
    /// only adds noise to them: <c>sizes</c> for encoded payload size, and <c>alloc</c> for
    /// bytes allocated per encode.
    /// </param>
    public static void Main(string[] args)
    {
        if (args is ["sizes", ..])
        {
            Sizes.Report();
            return;
        }

        if (args is ["alloc", ..])
        {
            Allocations.Report();
            return;
        }

        BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
    }
}
