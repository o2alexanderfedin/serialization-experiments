using System.Globalization;
using System.Text;
using SerializationExperiments.Tlv;

namespace SerializationExperiments.Benchmarks;

/// <summary>
/// Reports encoded size per document shape, against the equivalent XML text.
/// </summary>
/// <remarks>
/// Payload size is what the format is judged on, and it is exact rather than sampled, so it
/// belongs in a plain report rather than a timing harness.
/// </remarks>
internal static class Sizes
{
    internal static void Report()
    {
        Console.WriteLine("| Shape | Size | XML bytes | TLV bytes | Ratio |");
        Console.WriteLine("|---|---:|---:|---:|---:|");

        foreach (string shape in Documents.Shapes)
        {
            foreach (int count in new[] { 100, 1_000 })
        {
            Node tree = Documents.Build(shape, count);
            int tlv = TlvEncoder.Encode(tree).Length;
            int xml = Encoding.UTF8.GetByteCount(RenderXml(tree));

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {shape} | {count} | {xml:N0} | {tlv:N0} | {(double)tlv / xml:P1} |"));
            }
        }

        Console.WriteLine();
        Console.WriteLine(Documents.DepthCap);
    }

    private static string RenderXml(Node node)
    {
        var builder = new StringBuilder();
        RenderXml(node, builder);
        return builder.ToString();
    }

    private static void RenderXml(Node node, StringBuilder builder)
    {
        switch (node)
        {
            case TextNode text:
                builder.Append(text.Value);
                break;

            case TypedNode typed:
                // The nearest attribute-free XML equivalent of a type tag is an extra
                // wrapping element, so that is what the comparison charges it.
                builder.Append('<').Append(typed.TypeName).Append('>');
                RenderXml(typed.Inner, builder);
                builder.Append("</").Append(typed.TypeName).Append('>');
                break;

            case ElementNode element:
                builder.Append('<').Append(element.Name).Append('>');
                foreach (Node child in element.Children)
                {
                    RenderXml(child, builder);
                }

                builder.Append("</").Append(element.Name).Append('>');
                break;

            default:
                throw new InvalidOperationException($"Unsupported node type {node.GetType()}.");
        }
    }
}
