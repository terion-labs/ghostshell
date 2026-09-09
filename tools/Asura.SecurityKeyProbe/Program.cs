using Asura.Application;
using Asura.Infrastructure;

// Answers, with a key in hand, the three questions the design rests on:
// does a key enroll, does its secret repeat, and does the salt actually
// select the secret. Nothing here touches the profile or its keys — it is
// safe to run against a key that is already enrolled elsewhere.
ISecurityKeyAuthenticator authenticator = new Fido2SecurityKeyAuthenticator();

Console.WriteLine($"supported={authenticator.IsSupported}");
if (!authenticator.IsSupported)
{
    Console.WriteLine("libfido2 did not load; nothing else can be answered.");
    return 1;
}

Console.WriteLine($"key-attached={await authenticator.IsKeyPresentAsync(CancellationToken.None)}");

Console.WriteLine();
Console.WriteLine("Touch the key when it blinks — enrolling…");
var (enrollment, enrollFailure) = await authenticator.EnrollAsync(CancellationToken.None);
if (enrollment is null)
{
    Console.WriteLine($"ENROLL FAILED: {enrollFailure}");
    return 1;
}

Console.WriteLine(
    $"enrolled: credential={enrollment.CredentialId.Length} bytes, "
    + $"salt={enrollment.Salt.Length} bytes");

Console.WriteLine();
Console.WriteLine("Touch again — first derivation…");
var (first, firstFailure) = await authenticator.DeriveSecretAsync(
    enrollment,
    CancellationToken.None);
if (first is null)
{
    Console.WriteLine($"DERIVE FAILED: {firstFailure}");
    return 1;
}

Console.WriteLine($"derived {first.Length} bytes");

Console.WriteLine();
Console.WriteLine("Touch again — second derivation, same salt…");
var (second, secondFailure) = await authenticator.DeriveSecretAsync(
    enrollment,
    CancellationToken.None);
if (second is null)
{
    Console.WriteLine($"DERIVE FAILED: {secondFailure}");
    return 1;
}

// The property everything depends on: the same key and salt must give the
// same bytes, or a wrapped key could be sealed and never opened again.
var stable = first.AsSpan().SequenceEqual(second);
Console.WriteLine($"STABLE ACROSS CALLS: {stable}");

Console.WriteLine();
Console.WriteLine("Touch once more — a different salt must give different bytes…");
var (other, _) = await authenticator.DeriveSecretAsync(
    new SecurityKeyEnrollment(
        enrollment.CredentialId,
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
    CancellationToken.None);
var saltMatters = other is not null && !first.AsSpan().SequenceEqual(other);
Console.WriteLine($"SALT SELECTS THE SECRET: {saltMatters}");

Console.WriteLine();
Console.WriteLine(stable && saltMatters
    ? "PASS — the hardware behaves as the wrapped-key design assumes."
    : "FAIL — do not wire this to real keys until this passes.");
return stable && saltMatters ? 0 : 1;
