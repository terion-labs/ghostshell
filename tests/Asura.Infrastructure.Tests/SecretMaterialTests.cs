using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class SecretMaterialTests
{
    [Fact]
    public void Dispose_zeros_an_owned_buffer_and_blocks_future_reads()
    {
        byte[] owned = [11, 22, 33, 44];
        var material = SecretMaterial.TakeOwnership(owned);
        var copy = new byte[owned.Length];

        material.CopyTo(copy);
        material.Dispose();

        Assert.Equal([11, 22, 33, 44], copy);
        Assert.All(owned, value => Assert.Equal(0, value));
        Assert.True(material.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => material.CopyTo(copy));
        Assert.Equal("[secret material]", material.ToString());
    }

    [Fact]
    public void CopyFrom_does_not_alias_the_callers_buffer()
    {
        byte[] source = [1, 2, 3];
        using var material = SecretMaterial.CopyFrom(source);
        source.AsSpan().Clear();
        var copy = new byte[3];

        material.CopyTo(copy);

        Assert.Equal([1, 2, 3], copy);
    }

    [Fact]
    public void Empty_and_oversized_material_is_rejected_at_the_boundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SecretMaterial.CopyFrom([]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SecretMaterial.TakeOwnership(new byte[SecretMaterial.MaximumLength + 1]));
    }
}
