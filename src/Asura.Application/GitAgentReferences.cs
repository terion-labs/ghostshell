namespace Asura.Application;

public readonly record struct GitStateReferenceId : IGitOpaqueReference
{
    public GitStateReferenceId(string value) => Value = GitOpaqueReference.Validate(value);

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct GitChangeReferenceId : IGitOpaqueReference
{
    public GitChangeReferenceId(string value) => Value = GitOpaqueReference.Validate(value);

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct GitBranchReferenceId : IGitOpaqueReference
{
    public GitBranchReferenceId(string value) => Value = GitOpaqueReference.Validate(value);

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct GitRemoteReferenceId : IGitOpaqueReference
{
    public GitRemoteReferenceId(string value) => Value = GitOpaqueReference.Validate(value);

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct GitRemoteStateReferenceId : IGitOpaqueReference
{
    public GitRemoteStateReferenceId(string value) => Value = GitOpaqueReference.Validate(value);

    public string Value { get; }

    public override string ToString() => Value;
}

public interface IGitOpaqueReference
{
    string Value { get; }
}

internal static class GitOpaqueReference
{
    public static string Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "A Git reference must be an opaque bounded token.",
                nameof(value));
        }

        return value;
    }
}
