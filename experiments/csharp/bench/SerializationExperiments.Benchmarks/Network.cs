using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// What the codec numbers mean once the bytes have to cross the internet on a WebRTC data
/// channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured inputs, modelled network.</b> Encoded size and codec time are measured here.
/// Round-trip time, bandwidth and loss are not — no network is involved in this process — so
/// they are parameters of a model, and every latency figure below is arithmetic on top of two
/// measured numbers and three assumed ones. Treat the *ratios* as findings and the absolute
/// milliseconds as illustration.
/// </para>
/// <para>
/// The framing constants come from the specification rather than from guesswork. RFC 8831
/// §6.6 recommends user messages stay at or below 16 KiB, and a safe SCTP payload that avoids
/// IP fragmentation is about 1192 bytes once DTLS, SCTP and UDP/IP headers are removed from a
/// 1280-byte path MTU.
/// </para>
/// </remarks>
internal static class Network
{
    /// <summary>Usable SCTP payload per packet before IP fragmentation, in bytes.</summary>
    private const int SafePayload = 1192;

    /// <summary>RFC 8831 §6.6's recommended ceiling on a single user message.</summary>
    private const int RecommendedMessageCeilingBytes = 16 * 1024;

    /// <summary>Batch sizes: one record is a game tick, a thousand is a bulk sync.</summary>
    private static readonly int[] BatchSizes = [1, 10, 100, 1_000];

    private sealed record Link(string Name, double RttMs, double Mbps, double Loss);

    private static readonly Link[] Links =
    [
        new("same-metro fibre", 10, 100, 0.001),
        new("cross-country", 60, 25, 0.005),
        new("intercontinental", 180, 10, 0.01),
        new("mobile 4G", 70, 5, 0.02),
    ];

    internal static void Report()
    {
        Codec[] codecs = Formats.All.Where(c => c.Name != "TLV (tree + codec)").ToArray();

        Console.WriteLine("Measured: encoded size, encode time, decode time.");
        Console.WriteLine("Modelled: round-trip time, bandwidth, loss. Ratios are findings;");
        Console.WriteLine("absolute milliseconds are illustration.");
        Console.WriteLine();

        ReportFraming(codecs);
        ReportBudget(codecs);
        ReportCrossover(codecs);
    }

    /// <summary>Size, packet count, and whether a batch fits inside one WebRTC message.</summary>
    private static void ReportFraming(Codec[] codecs)
    {
        Console.WriteLine("## Framing — bytes, and SCTP packets at " +
                          $"{SafePayload} B usable payload");
        Console.WriteLine();
        Console.WriteLine("| Format | " + string.Join(" | ",
            BatchSizes.Select(n => $"{n} rec")) + " | 1000 raw pkts | 1000 brotli pkts | " +
            "over 16 KiB? |");
        Console.WriteLine("|---|" + string.Concat(BatchSizes.Select(_ => "---:|")) + "---:|---:|---|");

        foreach (Codec codec in codecs)
        {
            int[] sizes = BatchSizes
                .Select(n => codec.Encode(Profiles.All[2].Build(n)).Length)
                .ToArray();

            int bulk = sizes[^1];
            int compressed = Brotlied(codec.Encode(Profiles.All[2].Build(1_000)));

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {codec.Name} | {string.Join(" | ", sizes.Select(s => s.ToString("N0", CultureInfo.InvariantCulture)))} " +
                $"| {Packets(bulk)} | {Packets(compressed)} | " +
                $"{(bulk > RecommendedMessageCeilingBytes ? $"yes, {Math.Ceiling((double)bulk / RecommendedMessageCeilingBytes):N0} messages" : "no")} |"));
        }

        Console.WriteLine();
        Console.WriteLine($"A 1,000-record batch exceeds RFC 8831's {RecommendedMessageCeilingBytes / 1024} KiB");
        Console.WriteLine("recommendation for every format measured, so a real deployment splits it.");
        Console.WriteLine();

        // Record 0 is degenerate: its Guid is all zeros and its Count is zero, and protobuf
        // omits fields holding their type's default. A single-record measurement taken at
        // index 0 therefore flatters every format with that optimisation and charges full
        // price to every format without it. Record 500 has no default-valued field.
        Console.WriteLine("### One record, degenerate versus typical");
        Console.WriteLine();
        Console.WriteLine("| Format | record 0 | record 500 | difference |");
        Console.WriteLine("|---|---:|---:|---:|");

        Record[] typical = Profiles.All[2].Build(501)[500..];
        foreach (Codec codec in codecs)
        {
            int zero = codec.Encode(Profiles.All[2].Build(1)).Length;
            int mid = codec.Encode(typical).Length;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {codec.Name} | {zero} | {mid} | {mid - zero:+#;-#;0} |"));
        }

        Console.WriteLine();
    }

    /// <summary>Where the milliseconds actually go, end to end.</summary>
    private static void ReportBudget(Codec[] codecs)
    {
        Console.WriteLine("## Latency budget — one 100-record message, milliseconds");
        Console.WriteLine();

        Record[] rows = Profiles.All[2].Build(100);

        foreach (Link link in Links)
        {
            Console.WriteLine($"**{link.Name}** — {link.RttMs:N0} ms RTT, {link.Mbps:N0} Mbps, " +
                              $"{link.Loss:P1} loss");
            Console.WriteLine();
            Console.WriteLine("| Format | Bytes | Encode | Wire | Decode | CPU total | " +
                              "End to end | CPU share |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");

            foreach (Codec codec in codecs)
            {
                byte[] encoded = codec.Encode(rows);
                double encodeMs = TimeMs(() => codec.Encode(rows));
                double decodeMs = codec.Decode is null
                    ? 0
                    : TimeMs(() => { Matchup.Sink += codec.Decode(encoded); return 0; });

                double wireMs = encoded.Length * 8.0 / (link.Mbps * 1_000_000) * 1_000;
                double cpuMs = encodeMs + decodeMs;
                double totalMs = cpuMs + wireMs + link.RttMs;

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {codec.Name} | {encoded.Length:N0} | {encodeMs:F3} | {wireMs:F2} | " +
                    $"{decodeMs:F3} | {cpuMs:F3} | {totalMs:F1} | {cpuMs / totalMs:P1} |"));
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// The regime where codec speed stops being noise: a server fanning out to many peers.
    /// </summary>
    private static void ReportCrossover(Codec[] codecs)
    {
        Console.WriteLine("## Where CPU starts to matter — messages per second per core");
        Console.WriteLine();
        Console.WriteLine("Latency hides codec cost; throughput does not. One core, " +
                          "100-record messages,");
        Console.WriteLine("encode and decode, and the bandwidth one core's worth of output needs.");
        Console.WriteLine();
        Console.WriteLine("| Format | Encode+decode | Messages/s/core | Mbps to sustain it |");
        Console.WriteLine("|---|---:|---:|---:|");

        Record[] rows = Profiles.All[2].Build(100);

        foreach (Codec codec in codecs)
        {
            byte[] encoded = codec.Encode(rows);
            double encodeMs = TimeMs(() => codec.Encode(rows));
            double decodeMs = codec.Decode is null
                ? 0
                : TimeMs(() => { Matchup.Sink += codec.Decode(encoded); return 0; });

            double cpuMs = encodeMs + decodeMs;
            double perSecond = 1_000 / cpuMs;
            double mbps = perSecond * encoded.Length * 8 / 1_000_000;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {codec.Name} | {cpuMs:F3} ms | {perSecond:N0} | {mbps:N0} |"));
        }

        Console.WriteLine();
    }

    private static int Packets(int bytes) => (int)Math.Ceiling((double)bytes / SafePayload);

    /// <summary>Median of enough repetitions to clear the stopwatch's resolution.</summary>
    private static double TimeMs<T>(Func<T> action)
    {
        const int Warm = 200;
        const int Samples = 51;
        const int Inner = 20;

        for (int i = 0; i < Warm; i++)
        {
            action();
        }

        double[] taken = new double[Samples];
        for (int sample = 0; sample < Samples; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Inner; i++)
            {
                action();
            }

            taken[sample] = (Stopwatch.GetTimestamp() - start) * 1_000.0
                / Stopwatch.Frequency / Inner;
        }

        Array.Sort(taken);
        return taken[Samples / 2];
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
