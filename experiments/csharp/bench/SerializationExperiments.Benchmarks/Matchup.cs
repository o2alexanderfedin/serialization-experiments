using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Size and speed of every format in <see cref="Formats"/> over the same data.
/// </summary>
/// <remarks>
/// <para>
/// Size is exact and load-independent. Speed is not, so the timing harness is built for a
/// machine that is busy rather than one that is idle: each round measures every codec once,
/// the starting codec rotates by round so no codec always runs first, and the reported figure
/// is the median round rather than the mean. A load spike then lands on one round of one codec
/// instead of on whichever codec happened to be scheduled during it.
/// </para>
/// <para>
/// That makes the *ranking* robust. It does not make the absolute numbers publishable — for
/// those the machine has to be quiet, and BenchmarkDotNet is the right tool.
/// </para>
/// </remarks>
internal static class Matchup
{
    private const int Records = 1_000;
    private const int Rounds = 41;
    /// <summary>Rounds discarded before measuring.</summary>
    /// <remarks>
    /// Past the ~30-call threshold at which tiered compilation promotes a method to fully
    /// optimised code. Five rounds was not: the first profile measured was running tier-0 code
    /// throughout, which made every codec look 4-7x slower there than on a later profile and
    /// read exactly like a property of the data.
    /// </remarks>
    private const int WarmupRounds = 60;

    /// <summary>Keeps decode results reachable so the JIT cannot elide the work.</summary>
    internal static long Sink;

    internal static void Report()
    {
        Codec[] codecs = Formats.All;
        Record[][] data = new Record[Profiles.All.Length][];
        for (int index = 0; index < Profiles.All.Length; index++)
        {
            data[index] = Profiles.All[index].Build(Records);
        }

        Console.WriteLine($"{Records:N0} records per profile. Same data, every format.");
        Console.WriteLine();

        Verify(codecs, data[0]);
        ReportSize(codecs, data);
        Console.WriteLine();
        ReportSpeed(codecs, data);
    }

    /// <summary>
    /// Checks that every codec that reconstructs objects reconstructs the *same* objects.
    /// </summary>
    /// <remarks>
    /// A benchmark where one codec quietly drops a field is not a comparison, it is a
    /// handicap. Codecs that produce a tree rather than objects — TLV and the JSON DOM row —
    /// are checked only for having produced something, since their result is a different kind
    /// of thing and cannot share the checksum.
    /// </remarks>
    private static void Verify(Codec[] codecs, Record[] rows)
    {
        long expected = Formats.Checksum(rows);
        List<string> failures = [];

        foreach (Codec codec in codecs.Where(c => c.Decode is not null))
        {
            long actual = codec.Decode!(codec.Encode(rows));
            bool tree = codec.Name.StartsWith("TLV", StringComparison.Ordinal) || codec.Name == "JSON (DOM)";

            if (tree ? actual <= 0 : actual != expected)
            {
                failures.Add($"{codec.Name} returned {actual}, expected {(tree ? "> 0" : expected)}");
            }
        }

        Console.WriteLine(failures.Count == 0
            ? $"Round-trip check: all {codecs.Count(c => c.Decode is not null)} decoders agree."
            : "ROUND-TRIP FAILURES: " + string.Join("; ", failures));
        Console.WriteLine();
    }

    private static void ReportSize(Codec[] codecs, Record[][] data)
    {
        Console.WriteLine("## Size, bytes — exact, load-independent");
        Console.WriteLine();
        Console.WriteLine("| Format | Names on wire | " +
                          string.Join(" | ", Profiles.All.Select(p => p.Name)) + " |");
        Console.WriteLine("|---|---|" + string.Concat(Profiles.All.Select(_ => "---:|")));

        foreach (FieldIdentity identity in new[] { FieldIdentity.Named, FieldIdentity.Ordinal })
        {
            // The two TLV rows differ only in what the timing includes; their bytes are
            // identical, so the size table carries one of them.
            foreach (Codec codec in codecs.Where(c => c.Identity == identity && c.Name != "TLV (tree + codec)"))
            {
                byte[][] encoded = data.Select(codec.Encode).ToArray();
                string name = codec.Name == "TLV (codec only)" ? "TLV" : codec.Name;

                Row(name, identity, encoded.Select(b => b.Length));
                Row($"{name} + deflate", identity, encoded.Select(Deflated));
                Row($"{name} + brotli", identity, encoded.Select(Brotlied));
            }
        }

        static void Row(string label, FieldIdentity identity, IEnumerable<int> sizes) =>
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {label} | {(identity == FieldIdentity.Named ? "yes" : "no")} | " +
                $"{string.Join(" | ", sizes.Select(s => s.ToString("N0", CultureInfo.InvariantCulture)))} |"));
    }

    private static void ReportSpeed(Codec[] codecs, Record[][] data)
    {
        Console.WriteLine("## Speed, microseconds per 1,000 records — median of " +
                          $"{Rounds} interleaved rounds");
        Console.WriteLine();

        // Warm every codec on every profile before measuring any of them. Warming inside the
        // profile loop is not enough: the first profile measured then runs tier-0 code
        // throughout, and the result reads as a property of its data rather than of the
        // harness — CBOR encode came out 8x slower on the first profile than on the third,
        // over records of identical shape.
        foreach (Record[] warmRows in data)
        {
            for (int round = 0; round < WarmupRounds; round++)
            {
                foreach (Codec codec in codecs)
                {
                    byte[] warm = codec.Encode(warmRows);
                    Deflated(warm);
                    if (codec.Decode is not null)
                    {
                        Sink += codec.Decode(warm);
                    }
                }
            }
        }

        for (int profile = 0; profile < Profiles.All.Length; profile++)
        {
            if (Profiles.All[profile].Name is not ("repeated-guid" or "high-entropy"))
            {
                continue;
            }

            Record[] rows = data[profile];
            Console.WriteLine($"**{Profiles.All[profile].Name}**");
            Console.WriteLine();
            Console.WriteLine("| Format | Names | Encode | Encode + deflate | Decode |");
            Console.WriteLine("|---|---|---:|---:|---:|");

            Dictionary<string, (List<double> Encode, List<double> Deflate, List<double> Decode)> samples =
                codecs.ToDictionary(c => c.Name, _ => (new List<double>(), new List<double>(), new List<double>()));

            for (int round = 0; round < Rounds; round++)
            {

                for (int offset = 0; offset < codecs.Length; offset++)
                {
                    // Rotate the starting codec each round so none of them is permanently
                    // first, which would give it every cold cache and every scheduler hiccup.
                    Codec codec = codecs[(round + offset) % codecs.Length];
                    (List<double> encode, List<double> deflate, List<double> decode) = samples[codec.Name];

                    byte[] encoded = Time(() => codec.Encode(rows), out double encodeMicros);
                    Time(() => Deflated(encoded), out double deflateMicros);

                    double decodeMicros = double.NaN;
                    if (codec.Decode is not null)
                    {
                        // The result is accumulated into a field the JIT cannot prove unused,
                        // so the decode cannot be optimised away and clocked at zero.
                        Sink += Time(() => codec.Decode(encoded), out decodeMicros);
                    }

                    encode.Add(encodeMicros);
                    deflate.Add(encodeMicros + deflateMicros);
                    decode.Add(decodeMicros);
                }
            }

            foreach (Codec codec in codecs)
            {
                (List<double> encode, List<double> deflate, List<double> decode) = samples[codec.Name];
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {codec.Name} | {(codec.Identity == FieldIdentity.Named ? "yes" : "no")} | " +
                    $"{Median(encode):N1} | {Median(deflate):N1} | " +
                    $"{(codec.Decode is null ? "n/a" : Median(decode).ToString("N1", CultureInfo.InvariantCulture))} |"));
            }

            Console.WriteLine();
        }
    }

    private static T Time<T>(Func<T> action, out double microseconds)
    {
        long start = Stopwatch.GetTimestamp();
        T result = action();
        microseconds = (Stopwatch.GetTimestamp() - start) * 1_000_000.0 / Stopwatch.Frequency;
        return result;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0 || double.IsNaN(values[0]))
        {
            return double.NaN;
        }

        List<double> sorted = [.. values];
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static int Deflated(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return (int)output.Length;
    }

    private static int Brotlied(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(data);
        }

        return (int)output.Length;
    }
}
