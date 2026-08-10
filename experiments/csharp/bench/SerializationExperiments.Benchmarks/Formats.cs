using System.Buffers;
using System.Formats.Cbor;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Whether a format puts field *names* on the wire or only field *numbers*.
/// </summary>
/// <remarks>
/// The single most important axis for a fair comparison, and the one most easily fudged.
/// A named format can be read by a party that has never seen the schema; an ordinal one
/// cannot. Comparing the two on size alone rewards the ordinal format for a capability it
/// does not have, so the tables keep them in separate groups.
/// </remarks>
internal enum FieldIdentity
{
    /// <summary>Field names travel with the data.</summary>
    Named,

    /// <summary>Only field numbers travel; the reader must already know the schema.</summary>
    Ordinal,
}

/// <summary>One serializer under comparison.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Identity">Whether field names are on the wire.</param>
/// <param name="Encode">Produces the encoded bytes for a batch.</param>
/// <param name="Decode">
/// Reads the bytes back and returns a value derived from what it read, or
/// <see langword="null"/> if not applicable. The return value exists so the decode cannot be
/// optimised away: a decoder whose result is discarded can be elided, and would then post a
/// time that measures nothing.
/// </param>
internal sealed record Codec(
    string Name,
    FieldIdentity Identity,
    Func<Record[], byte[]> Encode,
    Func<byte[], long>? Decode);

/// <summary>
/// Every format in the comparison, configured so each represents the same data as well as it
/// is able.
/// </summary>
/// <remarks>
/// <para>
/// The fairness work is almost entirely in the <see cref="Guid"/> column. Left at its default,
/// MessagePack-CSharp writes a Guid as its 36-character spelling, which costs 38 bytes against
/// 17 for the raw 16 — a library default, not a property of the format, and charging
/// MessagePack for it would have been a rigged comparison. It gets a binary formatter here.
/// CBOR is hand-written against the low-level writer, so it writes a byte string directly.
/// </para>
/// <para>
/// Text formats keep the 36-character spelling because they have no alternative. That is a
/// real property of JSON and XML rather than a configuration choice, so it is charged to them.
/// </para>
/// </remarks>
internal static class Formats
{
    private static readonly XmlSerializer XmlCodec = new(typeof(Record[]));

    private static readonly MessagePackSerializerOptions BinaryGuidOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                [new BinaryGuidFormatter()],
                [StandardResolver.Instance]));

    private static readonly MessagePackSerializerOptions NamedOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                [new BinaryGuidFormatter()],
                [ContractlessStandardResolver.Instance]));

    internal static Codec[] All =>
    [
        // Two rows, because TLV is the only entrant with no object mapper — phase B is not
        // built. Everything else goes from objects to bytes in one step; TLV has to raise a
        // Node tree first. Timing only the codec flatters it, and timing the hand-written
        // tree-builder as though it were a mapper penalises it, so both are reported.
        new("TLV (codec only)", FieldIdentity.Named,
            rows => TlvEncoder.Encode(Tree(rows)),
            bytes => Nodes(TlvDecoder.Decode(bytes))),

        new("TLV (tree + codec)", FieldIdentity.Named,
            rows => TlvEncoder.Encode(Profiles.ToTlv(rows, ValueEncoding.Typed)),
            bytes => Nodes(TlvDecoder.Decode(bytes))),

        new("XML", FieldIdentity.Named, ToXml, FromXml),

        new("JSON", FieldIdentity.Named,
            rows => JsonSerializer.SerializeToUtf8Bytes(rows),
            bytes => Checksum(JsonSerializer.Deserialize<Record[]>(bytes)!)),

        // SQLite's JSONB. Size only: it is a database storage encoding rather than a client
        // codec, so timing a round trip would measure SQL and marshalling, not the format.
        new("JSONB (SQLite)", FieldIdentity.Named, ToJsonb, null),

        // TLV decodes to a generic node tree because no object mapper exists yet, so its
        // decode is DOM-level. JsonDocument is the honest counterpart for that column;
        // JSON (POCO) above is the typed comparison the other codecs are doing.
        new("JSON (DOM)", FieldIdentity.Named,
            rows => JsonSerializer.SerializeToUtf8Bytes(rows),
            bytes => JsonDocument.Parse(bytes).RootElement.GetArrayLength()),

        new("CBOR", FieldIdentity.Named, rows => ToCbor(rows, named: true), FromCborNamed),

        new("MessagePack", FieldIdentity.Named,
            rows => MessagePackSerializer.Serialize(ToNamed(rows), NamedOptions),
            bytes => Checksum(MessagePackSerializer.Deserialize<NamedRow[]>(bytes, NamedOptions))),

        new("CBOR (array)", FieldIdentity.Ordinal, rows => ToCbor(rows, named: false), FromCborArray),

        new("MessagePack (array)", FieldIdentity.Ordinal,
            rows => MessagePackSerializer.Serialize(rows, BinaryGuidOptions),
            bytes => Checksum(MessagePackSerializer.Deserialize<Record[]>(bytes, BinaryGuidOptions))),

        new("protobuf", FieldIdentity.Ordinal, ToProtobuf, FromProtobuf),
    ];

    /// <summary>
    /// The Node tree for a batch, built once and cached so it stays out of the timed region.
    /// </summary>
    /// <remarks>
    /// Keyed by array reference, which is safe here because the profiles build their batches
    /// once and reuse them for every round.
    /// </remarks>
    private static readonly Dictionary<Record[], Node> Trees = [];

    private static Node Tree(Record[] rows)
    {
        if (!Trees.TryGetValue(rows, out Node? tree))
        {
            tree = Profiles.ToTlv(rows, ValueEncoding.Typed);
            Trees[rows] = tree;
        }

        return tree;
    }

    private static byte[] ToXml(Record[] rows)
    {
        using var buffer = new MemoryStream();
        XmlCodec.Serialize(buffer, rows);
        return buffer.ToArray();
    }

    private static long FromXml(byte[] data)
    {
        using var buffer = new MemoryStream(data);
        return Checksum((Record[])XmlCodec.Deserialize(buffer)!);
    }

    private static byte[] ToProtobuf(Record[] rows)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, new RecordBatch { Rows = rows });
        return buffer.ToArray();
    }

    private static long FromProtobuf(byte[] data)
    {
        using var buffer = new MemoryStream(data);
        return Checksum(Serializer.Deserialize<RecordBatch>(buffer).Rows);
    }

    /// <summary>
    /// SQLite's binary JSON, produced from the same JSON text the JSON row measures.
    /// </summary>
    private static byte[] ToJsonb(Record[] rows)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select jsonb(@json)";
        command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(rows));
        return (byte[])command.ExecuteScalar()!;
    }

    private static byte[] ToCbor(Record[] rows, bool named)
    {
        var writer = new CborWriter();
        writer.WriteStartArray(rows.Length);

        foreach (Record row in rows)
        {
            if (named)
            {
                writer.WriteStartMap(6);
                writer.WriteTextString("DeviceId");
                writer.WriteByteString(row.DeviceId.ToByteArray());
                writer.WriteTextString("Timestamp");
                writer.WriteInt64(row.Timestamp);
                writer.WriteTextString("Value");
                writer.WriteDouble(row.Value);
                writer.WriteTextString("Ok");
                writer.WriteBoolean(row.Ok);
                writer.WriteTextString("Action");
                writer.WriteTextString(row.Action);
                writer.WriteTextString("Count");
                writer.WriteInt32(row.Count);
                writer.WriteEndMap();
                continue;
            }

            writer.WriteStartArray(6);
            writer.WriteByteString(row.DeviceId.ToByteArray());
            writer.WriteInt64(row.Timestamp);
            writer.WriteDouble(row.Value);
            writer.WriteBoolean(row.Ok);
            writer.WriteTextString(row.Action);
            writer.WriteInt32(row.Count);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        return writer.Encode();
    }

    private static long FromCborNamed(byte[] data)
    {
        var reader = new CborReader(data);
        int count = reader.ReadStartArray() ?? 0;
        Record[] rows = new Record[count];

        for (int index = 0; index < count; index++)
        {
            reader.ReadStartMap();
            var row = new Record();
            for (int field = 0; field < 6; field++)
            {
                switch (reader.ReadTextString())
                {
                    case "DeviceId": row.DeviceId = new Guid(reader.ReadByteString()); break;
                    case "Timestamp": row.Timestamp = reader.ReadInt64(); break;
                    case "Value": row.Value = reader.ReadDouble(); break;
                    case "Ok": row.Ok = reader.ReadBoolean(); break;
                    case "Action": row.Action = reader.ReadTextString(); break;
                    default: row.Count = reader.ReadInt32(); break;
                }
            }

            reader.ReadEndMap();
            rows[index] = row;
        }

        reader.ReadEndArray();
        return Checksum(rows);
    }

    private static long FromCborArray(byte[] data)
    {
        var reader = new CborReader(data);
        int count = reader.ReadStartArray() ?? 0;
        Record[] rows = new Record[count];

        for (int index = 0; index < count; index++)
        {
            reader.ReadStartArray();
            rows[index] = new Record
            {
                DeviceId = new Guid(reader.ReadByteString()),
                Timestamp = reader.ReadInt64(),
                Value = reader.ReadDouble(),
                Ok = reader.ReadBoolean(),
                Action = reader.ReadTextString(),
                Count = reader.ReadInt32(),
            };
            reader.ReadEndArray();
        }

        reader.ReadEndArray();
        return Checksum(rows);
    }

    /// <summary>A value derived from every field of every record, so no decode can be elided.</summary>
    /// <remarks>
    /// Doubles as the correctness check: every object-producing codec must return the same
    /// number, which fails loudly if a codec silently drops or mangles a field.
    /// </remarks>
    internal static long Checksum(Record[] rows)
    {
        long total = 0;
        foreach (Record row in rows)
        {
            total += row.Count + row.Timestamp + (long)row.Value
                + (row.Ok ? 1 : 0) + row.Action.Length + row.DeviceId.GetHashCode();
        }

        return total;
    }

    private static long Checksum(NamedRow[] rows)
    {
        long total = 0;
        foreach (NamedRow row in rows)
        {
            total += row.Count + row.Timestamp + (long)row.Value
                + (row.Ok ? 1 : 0) + row.Action.Length + row.DeviceId.GetHashCode();
        }

        return total;
    }

    /// <summary>Counts frames in a decoded TLV tree, so its decode cannot be elided either.</summary>
    private static long Nodes(Node node) => node switch
    {
        ElementNode element => 1 + element.Children.Sum(Nodes),
        _ => 1,
    };

    private static NamedRow[] ToNamed(Record[] rows)
    {
        NamedRow[] named = new NamedRow[rows.Length];
        for (int index = 0; index < rows.Length; index++)
        {
            named[index] = new NamedRow
            {
                DeviceId = rows[index].DeviceId,
                Timestamp = rows[index].Timestamp,
                Value = rows[index].Value,
                Ok = rows[index].Ok,
                Action = rows[index].Action,
                Count = rows[index].Count,
            };
        }

        return named;
    }
}

/// <summary>
/// The same fields without MessagePack's key attributes, so the contractless resolver writes
/// a map keyed by property name rather than a positional array.
/// </summary>
/// <remarks>
/// A separate type rather than a flag: <c>MessagePackObjectAttribute</c> on
/// <see cref="Record"/> pins it to array mode, and the whole point of this row is to measure
/// what MessagePack costs when it carries names, which is the only mode comparable with JSON,
/// XML, CBOR maps and TLV.
/// </remarks>
public sealed class NamedRow
{
    public Guid DeviceId { get; set; }

    public long Timestamp { get; set; }

    public double Value { get; set; }

    public bool Ok { get; set; }

    public string Action { get; set; } = string.Empty;

    public int Count { get; set; }
}

/// <summary>
/// Writes a <see cref="Guid"/> as its 16 raw bytes rather than its 36-character spelling.
/// </summary>
/// <remarks>
/// MessagePack-CSharp's default writes the string form, costing 38 bytes against 17. That is
/// a library default rather than a limit of the format, and leaving it in place would have
/// handed MessagePack a 21-byte-per-record penalty it does not deserve — on these profiles,
/// enough to change the ranking.
/// </remarks>
internal sealed class BinaryGuidFormatter : IMessagePackFormatter<Guid>
{
    public void Serialize(ref MessagePackWriter writer, Guid value, MessagePackSerializerOptions options)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        writer.Write(bytes);
    }

    public Guid Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        new(reader.ReadBytes()!.Value.ToArray().AsSpan());
}
