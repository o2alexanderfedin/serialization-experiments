using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Document shapes chosen to separate the costs the design trades against each other.
/// </summary>
internal static class Documents
{
    /// <summary>Shape names, in the order they should appear in a report.</summary>
    internal static readonly string[] Shapes =
        ["repeated", "unique", "deep", "text-heavy", "values-repeat", "values-unique"];

    /// <summary>Builds a shape by name, so every harness measures the same documents.</summary>
    /// <param name="shape">One of <see cref="Shapes"/>.</param>
    /// <param name="count">Element count, or chain depth for <c>deep</c>.</param>
    /// <returns>The document tree.</returns>
    internal static Node Build(string shape, int count) => shape switch
    {
        "repeated" => RepeatedNames(count),
        "unique" => UniqueNames(count),
        "deep" => Deep(count),
        "text-heavy" => TextHeavy(count, textLength: 200),
        "values-repeat" => ValuesRepeat(count),
        "values-unique" => ValuesUnique(count),
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
