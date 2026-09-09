using System.Text;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class SecretEditorViewModelTests
{
    [Fact]
    public void RelabelRequestTrimsMetadataWithoutCreatingSecretMaterial()
    {
        var editor = new SecretEditorViewModel(Secret())
        {
            Label = "  Production password  ",
        };

        var request = editor.CreateRelabelRequest();

        Assert.Equal(SecretEditAction.Relabel, request.Action);
        Assert.Equal("Production password", request.Label);
        Assert.Null(request.Replacement);
    }

    [Fact]
    public void RotateRequestTransfersUtf8MaterialAndClearsBoundInput()
    {
        var editor = new SecretEditorViewModel(Secret())
        {
            ReplacementValue = "pässword",
        };

        var request = editor.CreateRotateRequest();
        using var material = Assert.IsType<SecretMaterial>(request.Replacement);
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);

        Assert.Equal(SecretEditAction.Rotate, request.Action);
        Assert.Equal(Encoding.UTF8.GetBytes("pässword"), bytes);
        Assert.Equal(string.Empty, editor.ReplacementValue);
    }

    [Fact]
    public void EmptyReplacementFailsBeforeVaultMutation()
    {
        var editor = new SecretEditorViewModel(Secret());

        var error = Assert.Throws<ArgumentException>(() => editor.CreateRotateRequest());

        Assert.Contains("replacement", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SecretMetadataViewModel Secret() => new(
        new SecretRef("secret-test"),
        "Password",
        SecretKind.Password.ToString(),
        "Connection · connection-test",
        "Today",
        "Never",
        new SecretScope(SecretScopeKind.Connection, "connection-test"),
        "Used by: Production",
        1);
}
