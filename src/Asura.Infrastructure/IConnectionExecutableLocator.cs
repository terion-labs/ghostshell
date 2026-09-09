namespace Asura.Infrastructure;

public interface IConnectionExecutableLocator
{
    string? Find(string executable);
}
