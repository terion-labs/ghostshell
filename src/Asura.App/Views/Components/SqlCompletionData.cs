using Asura.Application;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Asura.App.Views.Components;

/// <summary>Adapts one provider-neutral completion item to AvaloniaEdit.</summary>
internal sealed class SqlCompletionData : ICompletionData
{
    private readonly SqlCompletionItem _item;

    internal SqlCompletionData(SqlCompletionItem item)
    {
        _item = item;
        Content = CreateContent(item);
    }

    public IImage? Image => null;

    public string Text => _item.Label;

    public object Content { get; }

    public object? Description => string.IsNullOrWhiteSpace(_item.Detail)
        ? null
        : _item.Detail;

    public double Priority => 0;

    internal string Label => _item.Label;

    internal SqlCompletionItemKind Kind => _item.Kind;

    internal string? Detail => _item.Detail;

    internal string InsertText => _item.InsertText;

    public void Complete(
        TextArea textArea,
        ISegment completionSegment,
        EventArgs insertionRequestEventArgs)
    {
        _ = insertionRequestEventArgs;
        textArea.Document.Replace(
            completionSegment.Offset,
            completionSegment.Length,
            _item.InsertText);
    }

    private static Control CreateContent(SqlCompletionItem item)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock { Text = item.Label });
        content.Children.Add(new TextBlock
        {
            Text = CompletionKindLabel(item.Kind),
            FontSize = 10,
            Opacity = 0.65,
        });
        return content;
    }

    private static string CompletionKindLabel(SqlCompletionItemKind kind) => kind switch
    {
        SqlCompletionItemKind.DataType => "type",
        SqlCompletionItemKind.Other => string.Empty,
        _ => kind.ToString().ToLowerInvariant(),
    };
}
