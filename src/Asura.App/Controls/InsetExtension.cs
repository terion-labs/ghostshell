using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Asura.App.Controls;

/// <summary>
/// A step on the shell's spacing scale, or nothing at all.
/// </summary>
internal enum Space
{
    /// <summary>No inset on this edge.</summary>
    None,

    Xs,
    Sm,
    Md,
    Lg,
    Xl,
    Xxl,
}

/// <summary>
/// Builds a <see cref="Thickness"/> from the spacing scale.
///
/// Uniform insets can be a plain token, but a margin is four numbers and the
/// markup needs fifty-one distinct combinations of them — far too many to name
/// individually, and naming them is how a scale turns back into a list of magic
/// values. This composes them instead:
///
/// <code>
/// Margin="{controls:Inset Md}"
/// Margin="{controls:Inset Horizontal=Lg, Vertical=Md}"
/// Margin="{controls:Inset Bottom=Sm}"
/// Margin="{controls:Inset Left=Sm, Scale=-1}"
/// </code>
///
/// It resolves to a multi-binding over the published <c>ShellSpace*</c> resources
/// rather than to a fixed number, so an inset written this way still reflows when
/// the density or text-scale setting changes. A markup extension that returned a
/// <see cref="Thickness"/> outright would be no better than the literal it
/// replaced — correct once, and frozen from then on.
/// </summary>
internal sealed class InsetExtension : MarkupExtension
{
    private static readonly IMultiValueConverter ToThickness = new ThicknessConverter();

    public InsetExtension()
    {
    }

    /// <summary>Supports the positional form, <c>{controls:Inset Md}</c>.</summary>
    public InsetExtension(Space all) => All = all;

    /// <summary>Every edge. Overridden by any more specific edge below.</summary>
    public Space All { get; set; }

    public Space Horizontal { get; set; }

    public Space Vertical { get; set; }

    public Space Left { get; set; }

    public Space Top { get; set; }

    public Space Right { get; set; }

    public Space Bottom { get; set; }

    /// <summary>
    /// Multiplies every resolved edge. A negative scale lets an affordance occupy
    /// the spacing gutter outside its own layout slot without freezing that
    /// gutter to one density's pixel value.
    /// </summary>
    public double Scale { get; set; } = 1;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding
        {
            Converter = ToThickness,
            ConverterParameter = Scale,
        };
        foreach (var edge in new[]
                 {
                     Resolve(Left, Horizontal),
                     Resolve(Top, Vertical),
                     Resolve(Right, Horizontal),
                     Resolve(Bottom, Vertical),
                 })
        {
            binding.Bindings.Add(Source(edge));
        }

        return binding;
    }

    /// <summary>
    /// The most specific instruction wins: an explicit edge, then the axis it sits
    /// on, then the value given for every edge.
    /// </summary>
    private Space Resolve(Space edge, Space axis) =>
        edge != Space.None ? edge
        : axis != Space.None ? axis
        : All;

    /// <summary>
    /// Every edge is a resource lookup, including the ones set to nothing —
    /// <c>ShellSpaceNone</c> is a published zero. Uniformity is the point: one
    /// binding kind means one code path, and no edge that behaves differently from
    /// its neighbours because it happened to be unset.
    /// </summary>
    private static BindingBase Source(Space space) =>
        new DynamicResourceExtension($"ShellSpace{space}");

    private sealed class ThicknessConverter : IMultiValueConverter
    {
        public object Convert(
            IList<object?> values,
            Type targetType,
            object? parameter,
            System.Globalization.CultureInfo culture)
        {
            var scale = parameter is double value && double.IsFinite(value)
                ? value
                : 1;
            return new Thickness(
                Edge(values, 0) * scale,
                Edge(values, 1) * scale,
                Edge(values, 2) * scale,
                Edge(values, 3) * scale);
        }

        /// <summary>
        /// An unresolved token reads as no inset rather than throwing. A window
        /// built before the appearance pipeline first publishes would otherwise
        /// take the whole application down over a margin.
        /// </summary>
        private static double Edge(IList<object?> values, int index) =>
            index < values.Count && values[index] is double value && double.IsFinite(value)
                ? value
                : 0;
    }
}
