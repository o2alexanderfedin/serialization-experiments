using System.Text;

namespace SerializationExperiments.Tests;

/// <summary>
/// Round-trip contract for the first <see cref="ISerializer"/> implementation.
/// Every future candidate should satisfy the same shape of test.
/// </summary>
public sealed class SystemTextJsonSerializerTests
{
    private readonly ISerializer serializer = new SystemTextJsonSerializer();

    /// <summary>Representative payload: nesting, a collection, and non-trivial scalars.</summary>
    public sealed record Order(
        int Id,
        string Customer,
        decimal Total,
        DateTimeOffset PlacedAt,
        IReadOnlyList<string> Tags);

    [Fact]
    public void Name_identifies_the_format()
    {
        Assert.Equal("System.Text.Json", this.serializer.Name);
    }

    [Fact]
    public void Round_trip_preserves_a_record()
    {
        var original = new Order(
            Id: 4711,
            Customer: "Ada Lovelace",
            Total: 1234.56m,
            PlacedAt: new DateTimeOffset(2026, 8, 6, 11, 41, 0, TimeSpan.FromHours(-7)),
            Tags: ["priority", "gift-wrap"]);

        byte[] payload = this.serializer.Serialize(original);
        Order decoded = this.serializer.Deserialize<Order>(payload);

        // Compared member-by-member deliberately: record equality is reference-based for
        // the Tags member. See Record_equality_is_not_structural_for_collection_members.
        Assert.Equal(original.Id, decoded.Id);
        Assert.Equal(original.Customer, decoded.Customer);
        Assert.Equal(original.Total, decoded.Total);
        Assert.Equal(original.PlacedAt, decoded.PlacedAt);
        Assert.Equal(original.Tags, decoded.Tags);
    }

    /// <summary>
    /// Documents a trap that will bite every candidate format, not just this one: a
    /// synthesized record <c>Equals</c> compares each member with
    /// <see cref="EqualityComparer{T}.Default"/>, which is reference equality for an
    /// <see cref="IReadOnlyList{T}"/>. A round-tripped record holding a collection is
    /// therefore never equal to its original, however faithful the serializer was.
    /// Correctness assertions in experiments must not lean on record equality.
    /// </summary>
    [Fact]
    public void Record_equality_is_not_structural_for_collection_members()
    {
        var original = new Order(1, "x", 0m, DateTimeOffset.UnixEpoch, ["a"]);

        Order decoded = this.serializer.Deserialize<Order>(this.serializer.Serialize(original));

        Assert.Equal(original.Tags, decoded.Tags);   // element-wise: equal
        Assert.NotEqual(original, decoded);          // record equality: not equal
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Round_trip_preserves_integers(int value)
    {
        Assert.Equal(value, this.serializer.Deserialize<int>(this.serializer.Serialize(value)));
    }

    [Fact]
    public void Round_trip_preserves_an_empty_collection()
    {
        IReadOnlyList<string> original = [];

        IReadOnlyList<string> decoded =
            this.serializer.Deserialize<IReadOnlyList<string>>(this.serializer.Serialize(original));

        Assert.Empty(decoded);
    }

    [Fact]
    public void Serialize_produces_utf8_json()
    {
        byte[] payload = this.serializer.Serialize(new { Greeting = "hallo" });

        Assert.Equal("{\"Greeting\":\"hallo\"}", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void Deserialize_rejects_a_malformed_payload()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ not json");

        SerializationFailedException error =
            Assert.Throws<SerializationFailedException>(() => this.serializer.Deserialize<Order>(garbage));

        Assert.Contains("System.Text.Json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_rejects_a_payload_that_decodes_to_null()
    {
        byte[] jsonNull = Encoding.UTF8.GetBytes("null");

        Assert.Throws<SerializationFailedException>(() => this.serializer.Deserialize<Order>(jsonNull));
    }
}
