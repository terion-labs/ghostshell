using Asura.Application.Previews;

namespace Asura.Application.Tests;

public sealed class PreviewRasterBudgetTests
{
    [Fact]
    public void A_portrait_smaller_than_the_budget_is_never_upscaled()
    {
        Assert.True(PreviewRasterBudget.TryFit(
            1_000,
            1_600,
            2_400,
            out var fitted));

        Assert.Equal(new PreviewRasterSize(1_000, 1_600), fitted);
    }

    [Theory]
    [InlineData(40_000, 30_000)]
    [InlineData(1_000, 100_000)]
    [InlineData(100_000, 1_000)]
    public void Oversized_dimensions_fit_both_the_dimension_and_pixel_budgets(
        int width,
        int height)
    {
        Assert.True(PreviewRasterBudget.TryFit(
            width,
            height,
            2_400,
            out var fitted));

        Assert.True(PreviewRasterBudget.Contains(fitted.Width, fitted.Height));
        Assert.InRange(fitted.Width, 1, width);
        Assert.InRange(fitted.Height, 1, height);
    }

    [Fact]
    public void An_extreme_page_aspect_ratio_is_rejected_when_one_pixel_cannot_fit()
    {
        Assert.False(PreviewRasterBudget.TryFitAspectRatio(
            1,
            100_000,
            1_600,
            out _));
    }

    [Fact]
    public void A_normal_page_fits_the_same_dimension_and_pixel_budget()
    {
        Assert.True(PreviewRasterBudget.TryFitAspectRatio(
            612,
            792,
            1_600,
            out var fitted));

        Assert.True(PreviewRasterBudget.Contains(fitted.Width, fitted.Height));
        Assert.Equal(1_600, fitted.Width);
    }
}
