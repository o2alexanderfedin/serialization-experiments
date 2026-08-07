using System.Text;
using SerializationExperiments.Tlv;

namespace SerializationExperiments.Tests.Tlv;

/// <summary>
/// Test helpers: terse tree construction and rendering back to XML text.
/// </summary>
/// <remarks>
/// Rendering exists because round-trip comparison is the only check that catches a name
/// table desync — length arithmetic and structural validation both pass on a document whose
/// names resolved to the wrong entries.
/// </remarks>
internal static class Xml
{
    internal static ElementNode Element(string name, params Node[] children) => new(name, children);

    internal static TextNode Text(string value) => new(value);

    /// <summary>Renders a tree to XML text, so two trees can be compared by content.</summary>
    internal static string Render(Node node)
    {
        var builder = new StringBuilder();
        Render(node, builder);
        return builder.ToString();
    }

    private static void Render(Node node, StringBuilder builder)
    {
        switch (node)
        {
            case TextNode text:
                builder.Append(text.Value);
                break;

            case ElementNode element:
                builder.Append('<').Append(element.Name).Append('>');
                foreach (Node child in element.Children)
                {
                    Render(child, builder);
                }

                builder.Append("</").Append(element.Name).Append('>');
                break;

            default:
                throw new InvalidOperationException($"Unsupported node type {node.GetType()}.");
        }
    }
}
