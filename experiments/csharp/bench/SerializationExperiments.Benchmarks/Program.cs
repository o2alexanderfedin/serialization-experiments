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
    /// <param name="args">BenchmarkDotNet arguments, e.g. <c>--filter</c> and <c>--job</c>.</param>
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
}
