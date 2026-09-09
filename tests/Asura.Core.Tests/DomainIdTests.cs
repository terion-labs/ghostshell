using System.Text.Json;

namespace Asura.Core.Tests;

public sealed class DomainIdTests
{
    [Fact]
    public void Durable_ids_reject_empty_values()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionId(" "));
        Assert.Throws<ArgumentException>(() => new SecretRef(string.Empty));
        Assert.Throws<ArgumentException>(() => new DefinitionKind("\t"));
    }

    [Fact]
    public void New_ids_are_non_empty_and_unique()
    {
        var first = LayoutId.New();
        var second = LayoutId.New();

        Assert.False(string.IsNullOrWhiteSpace(first.Value));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Durable_ids_round_trip_through_json_constructors()
    {
        var id = new ScreenId("operations-screen");

        var json = JsonSerializer.Serialize(id);
        var restored = JsonSerializer.Deserialize<ScreenId>(json);

        Assert.Equal(id, restored);
    }

    [Fact]
    public void Definition_kind_preserves_unknown_future_values()
    {
        var future = new DefinitionKind("future-provider-profile");
        var key = new DefinitionKey(future, "profile-7");

        var json = JsonSerializer.Serialize(key);
        var restored = JsonSerializer.Deserialize<DefinitionKey>(json);

        Assert.Equal(future, restored.Kind);
        Assert.Equal("future-provider-profile:profile-7", restored.ToString());
    }
}
