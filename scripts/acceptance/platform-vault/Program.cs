using System.Text.Json;

namespace Asura.PlatformVaultAcceptance;

internal static class Program
{
    private static readonly JsonSerializerOptions ReceiptJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args is ["self-test"])
        {
            return SelfTests.Run();
        }

        if (args is ["validate", var receiptToValidate])
        {
            return ValidateReceipt(receiptToValidate);
        }

        if (!TryParseRunArguments(args, out var receiptPath, out var repositoryRoot, out var dotnetPath))
        {
            PrintUsage();
            return 64;
        }

        AcceptanceReceipt receipt;
        try
        {
            var runner = new AcceptanceRunner(repositoryRoot, dotnetPath);
            receipt = await runner.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Acceptance runner failed ({exception.GetType().Name}).");
            receipt = AcceptanceRunner.CreateRunnerFailureReceipt();
        }

        var json = JsonSerializer.Serialize(receipt, ReceiptJson) + Environment.NewLine;
        var validationErrors = ReceiptValidator.Validate(json);
        if (validationErrors.Count != 0)
        {
            Console.Error.WriteLine("The generated receipt failed its own schema validator.");
            return 1;
        }

        WriteAtomically(receiptPath, json);
        Console.WriteLine($"{receipt.Status}: {receipt.Reason}");
        Console.WriteLine($"Receipt: {receiptPath}");
        return receipt.Status switch
        {
            "PASS" => 0,
            "BLOCKED" => 2,
            _ => 1,
        };
    }

    private static bool TryParseRunArguments(
        string[] args,
        out string receiptPath,
        out string repositoryRoot,
        out string dotnetPath)
    {
        receiptPath = string.Empty;
        repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory()) ?? string.Empty;
        dotnetPath = string.Empty;
        if (args.Length < 2 || args[0] != "run")
        {
            return false;
        }

        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }

            switch (args[index])
            {
                case "--receipt":
                    receiptPath = Path.GetFullPath(args[index + 1]);
                    break;
                case "--repository":
                    repositoryRoot = Path.GetFullPath(args[index + 1]);
                    break;
                case "--dotnet":
                    dotnetPath = Path.GetFullPath(args[index + 1]);
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(receiptPath))
        {
            return false;
        }

        dotnetPath = string.IsNullOrWhiteSpace(dotnetPath)
            ? ResolveDotnetPath(repositoryRoot)
            : dotnetPath;
        return !string.IsNullOrWhiteSpace(dotnetPath);
    }

    private static string ResolveDotnetPath(string repositoryRoot)
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var repositoryDotnet = Path.Combine(repositoryRoot, ".dotnet", executable);
        if (File.Exists(repositoryDotnet))
        {
            return repositoryDotnet;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var systemDotnet = path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, executable))
                .FirstOrDefault(File.Exists);
            if (systemDotnet is not null)
            {
                return systemDotnet;
            }
        }

        // Keep argument parsing successful so the runner can emit a BLOCKED receipt.
        return repositoryDotnet;
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static int ValidateReceipt(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("Receipt does not exist.");
            return 1;
        }

        var errors = ReceiptValidator.Validate(File.ReadAllText(path));
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("Receipt is valid.");
            return 0;
        }

        return 1;
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Receipt path must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  ... run --receipt <path> [--repository <path>] [--dotnet <path>]");
        Console.Error.WriteLine("  ... validate <receipt.json>");
        Console.Error.WriteLine("  ... self-test");
    }
}
