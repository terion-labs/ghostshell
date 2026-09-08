using System.Globalization;
using System.Numerics;
using System.Text;
using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Tests;

public sealed class DatabaseDetachedCellEditingTests
{
    [Theory]
    [InlineData(DatabaseArrayScalarType.Int128)]
    [InlineData(DatabaseArrayScalarType.UInt128)]
    [InlineData(DatabaseArrayScalarType.BigInteger)]
    public async Task LoadingSpilledNumberPreservesExactOriginalAndMutationIdentity(DatabaseArrayScalarType type)
    {
        const string text = "123";
        object expected = type switch
        {
            DatabaseArrayScalarType.Int128 => (object)Int128.Parse(text, CultureInfo.InvariantCulture),
            DatabaseArrayScalarType.UInt128 => UInt128.Parse(text, CultureInfo.InvariantCulture),
            _ => BigInteger.Parse(text, CultureInfo.InvariantCulture),
        };
        var kind = type == DatabaseArrayScalarType.UInt128 ? DatabaseValueKind.UnsignedInteger : DatabaseValueKind.SignedInteger;
        var content = new NumericContent(type, kind);
        var cell = new DatabaseResultCellViewModel(new DatabaseValue(content, kind, text),
            new DatabaseColumnDescriptor("key", "NUMERIC", kind, IsKey: true), 200, canEdit: true);
        await cell.LoadFullTextForEditingAsync(512L * 1024 * 1024, CancellationToken.None);
        Assert.Equal(expected.GetType(), cell.RawValue!.GetType());
        Assert.Equal(expected, cell.RawValue);
        Assert.Equal(expected, cell.BuildOriginalEdit().Value);
        Assert.True(cell.TryBuildEdit(out var untouched));
        Assert.Equal(expected, untouched.Value);
        Assert.False(cell.IsDirty);
        cell.EditText = "not a number";
        Assert.False(cell.TryBuildEdit(out _));
        Assert.Equal(expected, cell.BuildOriginalEdit().Value);
        cell.Reset();
        Assert.True(cell.TryBuildEdit(out var reset));
        Assert.Equal(expected, reset.Value);
        Assert.False(cell.IsDirty);
    }

    [Theory]
    [InlineData(1000, 700, 400, 300)]
    [InlineData(1000, 200, 600, 400)]
    [InlineData(1000, 1500, 400, 0)]
    [InlineData(0, 0, 0, 0)]
    public void EditorHeadroomAccountsForSystemAndNativeProcessLoad(long available, long load, long workingSet, long expected) =>
        Assert.Equal(expected, DatabaseRuntimePanelViewModel.EditorMemoryHeadroom(available, load, workingSet));

    private sealed class NumericContent(DatabaseArrayScalarType type, DatabaseValueKind kind) : DatabaseValueContent
    {
        public override long Length => 3;
        public override DatabaseValueKind Kind => kind;
        public override DatabaseArrayScalarType? ScalarType => type;
        public override Stream OpenRead() => new MemoryStream("123"u8.ToArray(), writable: false);
    }

    [Fact]
    public async Task CompleteTextLoadsOnlyOnExplicitEditorActionAndDoesNotBecomeDirty()
    {
        var original = new string('x', 30_000) + "🌐 complete tail";
        var content = new Content(Encoding.UTF8.GetBytes(original));
        var cell = Create(content);
        Assert.True(cell.NeedsFullTextForEditing);
        Assert.False(cell.IsDirty);
        Assert.Equal(string.Empty, cell.EditText);
        Assert.Throws<InvalidOperationException>(() => cell.FullText);
        Assert.Equal(0, content.Reads);
        await cell.LoadFullTextForEditingAsync(512L * 1024 * 1024, CancellationToken.None);
        Assert.Equal(original, cell.EditText);
        Assert.Equal(original, cell.RawValue);
        Assert.Equal(original, cell.FullText);
        Assert.False(cell.NeedsFullTextForEditing);
        Assert.False(cell.IsDirty);
        Assert.Equal(1, content.Reads);
        using var field = new DatabaseRowFieldViewModel(new DatabaseResultColumnViewModel(cell.Column, 200), cell);
        field.BeginEdit();
        Assert.Equal(original, field.Draft);
        cell.EditText += " edited";
        Assert.True(cell.IsDirty);
    }

    [Fact]
    public void ExplicitEmptyReplacementDoesNotNeedToReadTheOldContent()
    {
        var content = new Content("original"u8.ToArray());
        var cell = Create(content);
        cell.SetText(string.Empty);
        Assert.Equal(string.Empty, cell.RawValue);
        Assert.True(cell.IsDirty);
        Assert.Equal(0, content.Reads);
    }

    [Fact]
    public async Task InsufficientMemoryFailsBeforeOpeningContentAndKeepsItExportable()
    {
        var content = new Content("original"u8.ToArray());
        var cell = Create(content);
        var exception = await Assert.ThrowsAsync<IOException>(() => cell.LoadFullTextForEditingAsync(1, CancellationToken.None));
        Assert.Contains("Export the full value", exception.Message, StringComparison.Ordinal);
        Assert.True(cell.NeedsFullTextForEditing);
        Assert.Same(content, cell.RawValue);
        Assert.False(cell.IsDirty);
        Assert.Equal(0, content.Reads);
    }

    [Fact]
    public async Task CancelledEditorLoadDoesNotReadOrChangeTheCell()
    {
        var content = new Content("original"u8.ToArray());
        var cell = Create(content);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cell.LoadFullTextForEditingAsync(long.MaxValue, cancellation.Token));
        Assert.Equal(0, content.Reads);
        Assert.Same(content, cell.RawValue);
    }

    private static DatabaseResultCellViewModel Create(Content content) => new(
        new DatabaseValue(content, DatabaseValueKind.Text, "display preview", true),
        new DatabaseColumnDescriptor("value", "TEXT", DatabaseValueKind.Text), 200, canEdit: true);

    private sealed class Content(byte[] bytes) : DatabaseValueContent
    {
        public int Reads { get; private set; }
        public override long Length => bytes.Length;
        public override DatabaseValueKind Kind => DatabaseValueKind.Text;
        public override Stream OpenRead()
        {
            Reads++;
            return new MemoryStream(bytes, writable: false);
        }
    }
}
