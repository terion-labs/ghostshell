using Asura.App.ViewModels;

namespace Asura.App.Tests;

public sealed class DatabaseSpreadsheetRiskTests
{
    [Theory]
    [InlineData("=1+1", true)]
    [InlineData("+SUM(A1:A2)", true)]
    [InlineData("-1+1", true)]
    [InlineData("@SUM(A1:A2)", true)]
    [InlineData("\t\r\n =HYPERLINK(\"https://example.invalid\")", true)]
    [InlineData("\uFEFF =1", true)]
    [InlineData("hello = 1", false)]
    [InlineData("' =1", false)]
    [InlineData("", false)]
    public void FormulaNoticeIsAdvisoryAndDetectsLeadingControlSpace(string value, bool expected) =>
        Assert.Equal(expected, DatabaseSpreadsheetRisk.IsFormula(value));
}
