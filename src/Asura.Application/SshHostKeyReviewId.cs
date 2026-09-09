namespace Asura.Application;

public readonly record struct SshHostKeyReviewId
{
    public SshHostKeyReviewId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static SshHostKeyReviewId New() => new(Guid.CreateVersion7().ToString("N"));

    public override string ToString() => Value;
}
