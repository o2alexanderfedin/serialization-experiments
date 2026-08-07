using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Document shapes chosen to separate the costs the design trades against each other.
/// </summary>
internal static class Documents
{
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
