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

    internal static TypedNode Typed(string typeName, Node inner) => new(typeName, inner);

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

            case PrimitiveNode primitive:
                builder.Append('#').Append(primitive.Type.ToString("X2")).Append(':')
                       .Append(Convert.ToHexString(primitive.Payload.Span));
                break;

            case UnknownNode unknown:
                builder.Append('?').Append(unknown.Type.ToString("X2")).Append(':')
                       .Append(Convert.ToHexString(unknown.Payload.Span));
                break;

            case TypedNode typed:
                // Deliberately not element syntax: a TypedNode named "x" must not render the
                // same as an ElementNode named "x", or the round-trip check would not notice
                // one turning into the other.
                builder.Append('{').Append(typed.TypeName).Append(':');
                Render(typed.Inner, builder);
                builder.Append('}');
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
