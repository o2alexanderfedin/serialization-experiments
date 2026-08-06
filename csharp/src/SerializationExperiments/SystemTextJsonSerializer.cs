using System.Text.Json;

namespace SerializationExperiments;

/// <summary>
/// <see cref="ISerializer"/> backed by <see cref="JsonSerializer"/>.
/// </summary>
/// <remarks>
/// The baseline every other candidate is measured against: it ships in the box, so any
/// alternative has to justify its dependency by beating these numbers.
/// </remarks>
public sealed class SystemTextJsonSerializer : ISerializer
{
    private readonly JsonSerializerOptions options;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="options">
    /// Encoding options. Defaults to <see cref="JsonSerializerDefaults.General"/> so results
    /// reflect stock behaviour rather than a tuned configuration.
    /// </param>
    public SystemTextJsonSerializer(JsonSerializerOptions? options = null)
    {
        this.options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.General);
    }

    /// <inheritdoc />
    public string Name => "System.Text.Json";

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, this.options);
        }
        catch (JsonException ex)
        {
            throw new SerializationFailedException(
                $"{this.Name} could not encode {typeof(T)}.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new SerializationFailedException(
                $"{this.Name} does not support encoding {typeof(T)}.", ex);
        }
    }

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> data)
    {
        T? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<T>(data, this.options);
        }
        catch (JsonException ex)
        {
            throw new SerializationFailedException(
                $"{this.Name} could not decode {typeof(T)}: the payload is malformed.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new SerializationFailedException(
                $"{this.Name} does not support decoding {typeof(T)}.", ex);
        }

        return decoded ?? throw new SerializationFailedException(
            $"{this.Name} decoded a null value for {typeof(T)}.");
    }
}
