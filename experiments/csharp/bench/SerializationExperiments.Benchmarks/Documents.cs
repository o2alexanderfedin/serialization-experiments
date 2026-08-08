using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Document shapes chosen to separate the costs the design trades against each other.
/// </summary>
internal static class Documents
{
    /// <summary>Shape names, in the order they should appear in a report.</summary>
    internal static readonly string[] Shapes =
        ["repeated", "unique", "deep", "text-heavy", "values-repeat", "values-unique", "values-mixed", "typed"];

    /// <summary>
    /// Note for reports whose <c>deep</c> rows were clamped by the format's depth limit.
    /// </summary>
    internal static string DepthCap =>
        $"deep is clamped to the format limit of {TlvLimits.MaxDepth} frames, " +
        $"so its 1,000 row measures {TlvLimits.MaxDepth}.";

    /// <summary>Builds a shape by name, so every harness measures the same documents.</summary>
    /// <param name="shape">One of <see cref="Shapes"/>.</param>
    /// <param name="count">Element count, or chain depth for <c>deep</c>.</param>
    /// <returns>The document tree.</returns>
    /// <remarks>
    /// <c>deep</c> is clamped to <see cref="TlvLimits.MaxDepth"/>, so its 1,000 row measures
    /// 512 frames. Anything deeper is not a legal document — the encoder refuses it and no
    /// decoder would accept it — so measuring it would time something the codec never
    /// produces. <see cref="DepthCap"/> exists so reports can say this rather than imply the
    /// requested depth was used.
    /// </remarks>
    internal static Node Build(string shape, int count) => shape switch
    {
        "repeated" => RepeatedNames(count),
        "unique" => UniqueNames(count),
        "deep" => Deep(Math.Min(count, TlvLimits.MaxDepth)),
        "text-heavy" => TextHeavy(count, textLength: 200),
        "values-repeat" => ValuesRepeat(count),
        "values-unique" => ValuesUnique(count),
        "values-mixed" => ValuesMixed(count),
        "typed" => Typed(count),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown document shape."),
    };

    /// <summary>
    /// Many siblings sharing one tag name — the case interning is built for. Every element
    /// after the first encodes its name as a single byte.
    /// </summary>
    internal static Node RepeatedNames(int count)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = new ElementNode("line", [new TextNode($"value{index}")]);
        }

        return new ElementNode("list", children);
    }

    /// <summary>
    /// Every element a distinct name — interning never pays off, so this is the worst case
    /// for the name table and the upper bound on literal cost.
    /// </summary>
    internal static Node UniqueNames(int count)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = new ElementNode($"field{index}", [new TextNode($"value{index}")]);
        }

        return new ElementNode("record", children);
    }

    /// <summary>
    /// A single chain. Recursion depth equals node count, which is what the O(depth) memory
    /// claim is about.
    /// </summary>
    internal static Node Deep(int depth)
    {
        Node node = new TextNode("leaf");
        for (int level = depth; level > 0; level--)
        {
            node = new ElementNode($"level{level}", [node]);
        }

        return node;
    }

    /// <summary>
    /// Values drawn from a small vocabulary — status codes, country codes, enum-like data.
    /// The realistic case for value interning.
    /// </summary>
    /// <remarks>
    /// Deliberately identical to <see cref="ValuesUnique"/> in every respect except value
    /// repetition: same element name, same child count, same 10-character value length. The
    /// pair therefore isolates interning's effect from everything else.
    /// </remarks>
    internal static Node ValuesRepeat(int count, int vocabularySize = 10)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = new ElementNode(
                "status",
                [new TextNode(FormattableString.Invariant($"value-{index % vocabularySize:D4}"))]);
        }

        return new ElementNode("feed", children);
    }

    /// <summary>
    /// Every value distinct — the control for <see cref="ValuesRepeat"/>, and the shape
    /// where interning is pure overhead.
    /// </summary>
    internal static Node ValuesUnique(int count)
    {
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = new ElementNode(
                "status",
                [new TextNode(FormattableString.Invariant($"value-{index:D4}"))]);
        }

        return new ElementNode("feed", children);
    }

    /// <summary>
    /// A long head of distinct values, then a tail that repeats a small vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other shapes cannot show what id density is worth. Their repeated values appear
    /// near the start of the document, so they claim low ids and their references stay one
    /// varint byte whether or not distinct values also claim ids.
    /// </para>
    /// <para>
    /// Here the repeating vocabulary is not seen until most of the document has gone by — a
    /// log whose recurring messages start late, or a table whose status column only becomes
    /// repetitive after a header of distinct fields. If every distinct value claims an id,
    /// the vocabulary lands past 127 and each reference costs two varint bytes instead of one.
    /// </para>
    /// </remarks>
    internal static Node ValuesMixed(int count, int vocabularySize = 4)
    {
        int head = count * 4 / 5;
        Node[] children = new Node[count];

        for (int index = 0; index < count; index++)
        {
            string value = index < head
                ? FormattableString.Invariant($"value-{index:D4}")
                : FormattableString.Invariant($"state-{index % vocabularySize:D4}");

            children[index] = new ElementNode("status", [new TextNode(value)]);
        }

        return new ElementNode("feed", children);
    }

    /// <summary>
    /// <see cref="ValuesRepeat"/> with every child wrapped in a type tag.
    /// </summary>
    /// <remarks>
    /// A matched pair with <c>values-repeat</c>, in the same spirit as
    /// <c>values-repeat</c>/<c>values-unique</c>: identical element names, identical values,
    /// identical child count. The only difference is the type tag on each child, so the gap
    /// between the two shapes is exactly what polymorphism costs — nothing else moves.
    /// </remarks>
    internal static Node Typed(int count, int typeCount = 4)
    {
        var untyped = (ElementNode)ValuesRepeat(count);
        Node[] children = new Node[count];

        for (int index = 0; index < count; index++)
        {
            children[index] = new TypedNode(
                FormattableString.Invariant($"Shape{index % typeCount}"),
                untyped.Children[index]);
        }

        return new ElementNode("feed", children);
    }

    /// <summary>
    /// Repeated names with substantial text, so payload dominates structure — the shape
    /// where a buffering encoder would hold the most memory.
    /// </summary>
    internal static Node TextHeavy(int count, int textLength)
    {
        string text = new('x', textLength);
        Node[] children = new Node[count];
        for (int index = 0; index < count; index++)
        {
            children[index] = new ElementNode("body", [new TextNode(text)]);
        }

        return new ElementNode("document", children);
    }
}
