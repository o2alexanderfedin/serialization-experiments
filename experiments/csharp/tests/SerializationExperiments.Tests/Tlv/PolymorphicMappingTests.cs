using System.Globalization;
using SerializationExperiments.Tlv;
using static SerializationExperiments.Tests.Tlv.Xml;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// A worked example: mapping a C# type hierarchy through <see cref="TypedNode"/>, the way a
/// caller is meant to.
/// </summary>
/// <remarks>
/// <para>
/// The mapper lives here, in caller code, and not in the library. That is the design, not an
/// omission. Every mapping is a line somebody wrote; there is no reflection, no
/// <c>Type.GetType</c>, no assembly scan, and no path by which a name in a document can
/// reach a constructor that is not in the table below.
/// </para>
/// <para>
/// The alternative — a library that resolves embedded type names for you — is the one with
/// the track record. <c>BinaryFormatter</c> was removed from .NET 9 over it. Avro shipped
/// three successive versions of a single allow-list before it held: the first checked after
/// the class had already been loaded and its static initialisers had run, the second still
/// trusted whole namespaces, and a second call path was never covered at all.
/// </para>
/// </remarks>
public sealed class PolymorphicMappingTests
{
    private abstract record Shape;

    private sealed record Circle(double Radius) : Shape;

    private sealed record Square(double Side) : Shape;

    private sealed record Drawing(IReadOnlyList<Shape> Shapes);

    /// <summary>
    /// The allow-list. A name absent from it is never resolved, however well-formed.
    /// </summary>
    private static readonly Dictionary<string, Func<Node, Shape>> Readers = new(StringComparer.Ordinal)
    {
        ["Circle"] = node => new Circle(ReadNumber(node, "radius")),
        ["Square"] = node => new Square(ReadNumber(node, "side")),
    };

    [Fact]
    public void A_hierarchy_round_trips_through_the_wire()
    {
        var drawing = new Drawing([new Circle(3.5), new Square(2), new Circle(1.25)]);

        Drawing decoded = ReadDrawing(TlvDecoder.Decode(TlvEncoder.Encode(WriteDrawing(drawing))));

        Assert.Equal(drawing.Shapes.Count, decoded.Shapes.Count);
        Assert.Equal(drawing.Shapes, decoded.Shapes);
    }

    [Fact]
    public void The_derived_type_is_what_comes_back_not_the_base()
    {
        var drawing = new Drawing([new Square(4)]);

        Drawing decoded = ReadDrawing(TlvDecoder.Decode(TlvEncoder.Encode(WriteDrawing(drawing))));

        Square square = Assert.IsType<Square>(Assert.Single(decoded.Shapes));
        Assert.Equal(4, square.Side);
    }

    [Fact]
    public void A_repeated_type_name_is_written_once_however_many_instances()
    {
        var drawing = new Drawing([new Circle(1), new Circle(2), new Circle(3), new Circle(4)]);

        byte[] encoded = TlvEncoder.Encode(WriteDrawing(drawing));

        Assert.Equal(1, CountOccurrences(encoded, "Circle"u8));
    }

    [Fact]
    public void An_unknown_type_name_is_refused_by_the_mapper_not_the_decoder()
    {
        // The document decodes perfectly — it is well-formed, and the decoder has no opinion
        // about type names. The refusal happens in caller code, against the caller's list.
        Node hostile = Element("drawing", Typed("System.Diagnostics.Process", Element("shape", Text("1"))));
        Node decoded = TlvDecoder.Decode(TlvEncoder.Encode(hostile));

        Assert.IsType<ElementNode>(decoded);

        KeyNotFoundException error = Assert.Throws<KeyNotFoundException>(() => ReadDrawing(decoded));
        Assert.Contains("Process", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_type_can_be_forwarded_untouched_instead_of_refused()
    {
        // A relay that does not know every type can still pass a document on byte-for-byte,
        // because the type name survives decoding as text and re-encodes unchanged. Neither
        // System.Text.Json nor Avro can do this: one drops or fails on an unrecognised
        // discriminator, the other cannot even skip the value.
        byte[] original = TlvEncoder.Encode(
            Element("drawing", Typed("Triangle", Element("shape", Text("3")))));

        Node relayed = TlvDecoder.Decode(original);

        Assert.Equal(original, TlvEncoder.Encode(relayed));
    }

    [Fact]
    public void Adding_a_derived_type_does_not_break_an_older_reader()
    {
        // A writer that knows Triangle, a reader that does not. The reader still handles
        // every shape it recognises rather than failing the whole document.
        Node fromNewerWriter = Element(
            "drawing",
            Typed("Circle", Element("radius", Text("1"))),
            Typed("Triangle", Element("base", Text("2"))),
            Typed("Square", Element("side", Text("3"))));

        Node decoded = TlvDecoder.Decode(TlvEncoder.Encode(fromNewerWriter));

        List<Shape> known = [];
        List<string> skipped = [];
        foreach (Node child in ((ElementNode)decoded).Children)
        {
            TypedNode typed = Assert.IsType<TypedNode>(child);
            if (Readers.TryGetValue(typed.TypeName, out Func<Node, Shape>? read))
            {
                known.Add(read(typed.Inner));
            }
            else
            {
                skipped.Add(typed.TypeName);
            }
        }

        Assert.Equal([new Circle(1), new Square(3)], known);
        Assert.Equal(["Triangle"], skipped);
    }

    private static Node WriteDrawing(Drawing drawing)
    {
        Node[] children = new Node[drawing.Shapes.Count];
        for (int index = 0; index < drawing.Shapes.Count; index++)
        {
            children[index] = drawing.Shapes[index] switch
            {
                Circle circle => Typed("Circle", Number("radius", circle.Radius)),
                Square square => Typed("Square", Number("side", square.Side)),

                // Exhaustive by construction: a new derived type is a compile-time hole here,
                // not a runtime surprise at the far end of the wire.
                _ => throw new ArgumentException(
                    $"No mapping for {drawing.Shapes[index].GetType().Name}.", nameof(drawing)),
            };
        }

        return new ElementNode("drawing", children);
    }

    private static Drawing ReadDrawing(Node node)
    {
        var element = (ElementNode)node;
        List<Shape> shapes = [];

        foreach (Node child in element.Children)
        {
            var typed = (TypedNode)child;

            // The one and only place a name becomes a type. It is a dictionary lookup
            // against a table written by hand; an unknown key throws rather than resolving.
            shapes.Add(Readers[typed.TypeName](typed.Inner));
        }

        return new Drawing(shapes);
    }

    private static Node Number(string name, double value) =>
        Element(name, Text(value.ToString(CultureInfo.InvariantCulture)));

    private static double ReadNumber(Node node, string expectedName)
    {
        var element = (ElementNode)node;
        Assert.Equal(expectedName, element.Name);
        return double.Parse(((TextNode)element.Children[0]).Value, CultureInfo.InvariantCulture);
    }

    private static int CountOccurrences(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        int count = 0;
        for (int index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle))
            {
                count++;
            }
        }

        return count;
    }
}
