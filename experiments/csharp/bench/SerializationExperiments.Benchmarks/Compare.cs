using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using MessagePack;
using ProtoBuf;
using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Reports encoded size against other serialization formats, and against a general-purpose
/// compressor.
/// </summary>
/// <remarks>
/// <para>
/// Every measurement in this repository until now has compared TLV against a different version
/// of TLV, which says whether the last change helped but nothing about whether the format is
/// worth building. This report supplies the missing reference points: a self-describing peer
/// (MessagePack), a schema-driven one (protobuf), the format everyone actually uses (JSON),
/// and deflate chained onto each.
/// </para>
/// <para>
/// The deflate column is the one that matters most. Value interning is a hand-maintained
/// dictionary compressor, and deflate is a well-tuned one; if deflate over an un-interned
/// document matches interning, then interning is duplicating zlib at the cost of a table, an
/// id space, and the desynchronisation bug class that comes with them.
/// </para>
/// <para>
/// Sizes are exact and load-independent, so this report is trustworthy on a busy machine in a
/// way that a timing benchmark is not.
/// </para>
/// </remarks>
internal static class Compare
{
    private const int Records = 1_000;

    internal static void Report()
    {
        Console.WriteLine($"Encoded size, {Records:N0} records per profile. Exact bytes.");
        Console.WriteLine();
        Console.WriteLine($"Value interning: {(InterningIsOn() ? "ON" : "OFF")}");
        Console.WriteLine();

        Payload[] payloads = new Payload[Profiles.All.Length];
        for (int i = 0; i < Profiles.All.Length; i++)
        {
            Record[] rows = Profiles.All[i].Build(Records);
            payloads[i] = new Payload(
                Profiles.All[i].Name,
                TlvEncoder.Encode(Profiles.ToTlv(rows, ValueEncoding.Typed)),
                TlvEncoder.Encode(Profiles.ToTlv(rows, ValueEncoding.TypedInternableGuid)),
                TlvEncoder.Encode(Profiles.ToTlv(rows, ValueEncoding.Text)),
                JsonSerializer.SerializeToUtf8Bytes(rows),
                MessagePackSerializer.Serialize(rows),
                ToProtobuf(rows));
        }

        Header("Raw");
        foreach (Payload p in payloads)
        {
            Row(p, static bytes => bytes.Length);
        }

        Console.WriteLine();
        Header("Deflate, CompressionLevel.Optimal");
        foreach (Payload p in payloads)
        {
            Row(p, Deflated);
        }

        Console.WriteLine();
        Console.WriteLine("TLV hybrid = typed values, except a Guid emitted as text so that the");
        Console.WriteLine("existing value table can intern it. It simulates the effect of allowing");
        Console.WriteLine("fixed-width primitives to claim value ids, which phase A ruled out.");
    }

    private static void Header(string title)
    {
        Console.WriteLine($"**{title}**");
        Console.WriteLine();
        Console.WriteLine("| Profile | TLV typed | TLV hybrid | TLV text | JSON | MsgPack | protobuf |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");
    }

    private static void Row(Payload p, Func<byte[], int> size) => Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"| {p.Name} | {size(p.TlvTyped):N0} | {size(p.TlvHybrid):N0} | {size(p.TlvText):N0} | " +
        $"{size(p.Json):N0} | {size(p.MsgPack):N0} | {size(p.Protobuf):N0} |"));

    private sealed record Payload(
        string Name,
        byte[] TlvTyped,
        byte[] TlvHybrid,
        byte[] TlvText,
        byte[] Json,
        byte[] MsgPack,
        byte[] Protobuf);

    /// <summary>
    /// Detects whether value interning is compiled in, so the report cannot mislabel itself.
    /// </summary>
    /// <remarks>
    /// The 2x2 is run by patching <c>MinInternedValueLength</c> and rebuilding. A report that
    /// stated the configuration from a command-line flag rather than from observed behaviour
    /// would happily print "OFF" for a build where the patch silently failed to apply — which
    /// is exactly how a mutation experiment in this repository once produced a result from an
    /// unrun mutation.
    /// </remarks>
    private static bool InterningIsOn()
    {
        // Self-calibrating: two identical values against two distinct values of the same
        // length. Interning makes the repeated pair smaller; without it the two are equal.
        // Deliberately no magic threshold — the first version of this probe compared against a
        // hand-computed constant and got it wrong by one byte, so it would have reported "ON"
        // for an interning-off build and mislabelled the entire report.
        Node same = new ElementNode("r", [new TextNode("abcdefgh"), new TextNode("abcdefgh")]);
        Node distinct = new ElementNode("r", [new TextNode("abcdefgh"), new TextNode("ijklmnop")]);
        return TlvEncoder.Encode(same).Length < TlvEncoder.Encode(distinct).Length;
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

    private static byte[] ToProtobuf(Record[] rows)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, new RecordBatch { Rows = rows });
        return buffer.ToArray();
    }
}

/// <summary>
/// One row of test data, shaped to cover the cases the size question turns on.
/// </summary>
/// <remarks>
/// A single record type across all profiles, with the profiles varying the *values* rather
/// than the schema. That keeps protobuf's field numbering and MessagePack's key numbering
/// constant, so a difference between two rows of the report is a difference in the data and
/// never in how a competitor was configured.
/// </remarks>
[MessagePackObject]
[ProtoContract]
public sealed class Record
{
    [Key(0)][ProtoMember(1)] public Guid DeviceId { get; set; }

    [Key(1)][ProtoMember(2)] public long Timestamp { get; set; }

    [Key(2)][ProtoMember(3)] public double Value { get; set; }

    [Key(3)][ProtoMember(4)] public bool Ok { get; set; }

    [Key(4)][ProtoMember(5)] public string Action { get; set; } = string.Empty;

    [Key(5)][ProtoMember(6)] public int Count { get; set; }
}

/// <summary>Protobuf has no top-level repeated field, so the batch needs a wrapper.</summary>
[ProtoContract]
public sealed class RecordBatch
{
    [ProtoMember(1)] public Record[] Rows { get; set; } = [];
}

/// <summary>A named way of filling <see cref="Record"/> with a particular value profile.</summary>
internal sealed record Profile(string Name, Func<int, Record[]> Build);

/// <summary>How a record's scalar values are put on the wire.</summary>
internal enum ValueEncoding
{
    /// <summary>Everything stringified, as before phase A.</summary>
    Text,

    /// <summary>Phase A's typed frames throughout.</summary>
    Typed,

    /// <summary>
    /// Typed, except a <see cref="System.Guid"/> rides as text so the value table can intern
    /// it — a stand-in for letting fixed-width primitives claim value ids.
    /// </summary>
    TypedInternableGuid,
}

internal static class Profiles
{
    private static readonly string[] Actions =
        ["start", "stop", "pause", "resume", "error", "retry", "flush", "close"];

    internal static readonly Profile[] All =
    [
        // The case that made phase A's interning decision look wrong: a handful of device ids
        // repeating across every record, which interning collapses to a three-byte reference
        // and a GUID frame cannot.
        new("repeated-guid", count => Make(count, i => new Record
        {
            DeviceId = Deterministic(i % 10),
            Timestamp = 1_700_000_000_000 + (i * 1_000L),
            Value = Math.Round(20.0 + (i % 40) * 0.5, 1),
            Ok = i % 7 != 0,
            Action = Actions[i % Actions.Length],
            Count = i % 100,
        })),

        // The opposite: every identifier unique, so interning has nothing to collapse.
        new("distinct-guid", count => Make(count, i => new Record
        {
            DeviceId = Deterministic(i),
            Timestamp = 1_700_000_000_000 + (i * 1_000L),
            Value = i * 1.7320508075688772,
            Ok = i % 3 != 0,
            Action = Actions[i % Actions.Length],
            Count = i,
        })),

        // Numbers that do not spell short: the case typed values are supposed to win.
        new("high-entropy", count => Make(count, i => new Record
        {
            DeviceId = Deterministic(i),
            Timestamp = 1_700_000_000_000 + (i * 7_919L),
            Value = Math.PI * (i + 1) / 7.0,
            Ok = (i & 1) == 0,
            Action = Actions[i % Actions.Length],
            Count = i * 65_537,
        })),

        // Values that spell short and repeat: the case interning is supposed to win.
        new("low-entropy", count => Make(count, i => new Record
        {
            DeviceId = Deterministic(0),
            Timestamp = 1_700_000_000_000,
            Value = 0.5,
            Ok = true,
            Action = Actions[i % 2],
            Count = i % 10,
        })),
    ];

    private static Record[] Make(int count, Func<int, Record> build)
    {
        Record[] rows = new Record[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = build(i);
        }

        return rows;
    }

    /// <summary>
    /// Builds the TLV tree for a batch, either with phase A's typed values or with everything
    /// stringified as it was before phase A.
    /// </summary>
    /// <remarks>
    /// Fields are <c>ELEMENT</c> frames because phase B's <c>FIELD</c> frame is not built yet.
    /// <c>FIELD</c> has <c>ELEMENT</c>'s exact layout — type, length, name reference, one
    /// child — so this measures phase B's framing cost precisely regardless.
    /// </remarks>
    internal static Node ToTlv(Record[] rows, ValueEncoding encoding)
    {
        bool typed = encoding != ValueEncoding.Text;
        List<Node> records = new(rows.Length);
        foreach (Record row in rows)
        {
            records.Add(new ElementNode("rec",
            [
                new ElementNode("DeviceId", [typed && encoding != ValueEncoding.TypedInternableGuid
                    ? Primitives.Guid(row.DeviceId)
                    : new TextNode(row.DeviceId.ToString())]),
                new ElementNode("Timestamp", [typed
                    ? Primitives.Int(row.Timestamp)
                    : new TextNode(row.Timestamp.ToString(CultureInfo.InvariantCulture))]),
                new ElementNode("Value", [typed
                    ? Primitives.Double(row.Value)
                    : new TextNode(row.Value.ToString("R", CultureInfo.InvariantCulture))]),
                new ElementNode("Ok", [typed
                    ? Primitives.Bool(row.Ok)
                    : new TextNode(row.Ok ? "true" : "false")]),
                new ElementNode("Action", [new TextNode(row.Action)]),
                new ElementNode("Count", [typed
                    ? Primitives.Int(row.Count)
                    : new TextNode(row.Count.ToString(CultureInfo.InvariantCulture))]),
            ]));
        }

        return new ElementNode("batch", records);
    }

    private static Guid Deterministic(int i)
    {
        byte[] bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes, i);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), i * 2654435761L);
        return new Guid(bytes);
    }
}
