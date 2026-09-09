namespace Asura.App.ViewModels;

public interface IProductComponentCatalog
{
    IReadOnlyList<ProductComponentViewModel> Components { get; }
}
