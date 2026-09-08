using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docking;

namespace GhostShell.App;

/// <summary>
/// Owns the user-facing definition bundle workflow: native path selection, bounded/strict file
/// encoding, safety preflight, explicit commit, and presentation reload.
/// </summary>
public sealed class DefinitionBundleController
{
    public const long MaximumImportBytes = 32L * 1024 * 1024;
    public const string SuggestedExportFileName = "ghostshell-definitions.json";

    private readonly IDefinitionBundleStore _bundleStore;
    private readonly IDefinitionBundlePathPicker _pathPicker;
    private readonly IDefinitionBundleImportRefresh _importRefresh;
    private readonly WorkspaceDefinitionOccupancy _workspaceDefinitionOccupancy;

    public DefinitionBundleController(
        IDefinitionBundleStore bundleStore,
        IDefinitionBundlePathPicker pathPicker,
        IDefinitionBundleImportRefresh importRefresh,
        WorkspaceDefinitionOccupancy workspaceDefinitionOccupancy)
    {
        _bundleStore = bundleStore ?? throw new ArgumentNullException(nameof(bundleStore));
        _pathPicker = pathPicker ?? throw new ArgumentNullException(nameof(pathPicker));
        _importRefresh = importRefresh ?? throw new ArgumentNullException(nameof(importRefresh));
        _workspaceDefinitionOccupancy = workspaceDefinitionOccupancy
            ?? throw new ArgumentNullException(nameof(workspaceDefinitionOccupancy));
    }

    public async ValueTask<DefinitionStoreResult<DefinitionBundleExportReceipt>> ExportAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var selectedPath = await _pathPicker.PickExportPathAsync(
                    SuggestedExportFileName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (selectedPath is null)
            {
                return Cancelled<DefinitionBundleExportReceipt>("The definition export was cancelled.");
            }

            var path = NormalizeSelectedPath(selectedPath);
            var exported = await _bundleStore.ExportAsync(cancellationToken).ConfigureAwait(false);
            if (!exported.IsSuccess)
            {
                return Failure<DefinitionBundleExportReceipt>(exported.Error!);
            }

            var bundle = exported.Value!;

            // Export uses the same strict parser as import as a defense-in-depth proof that the
            // selected file cannot receive an unsafe or unmapped payload, including secret values.
            var safety = await _bundleStore.PreflightImportAsync(
                    bundle,
                    DefinitionImportMode.ReplaceExisting,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!safety.IsSuccess)
            {
                return Failure<DefinitionBundleExportReceipt>(safety.Error!);
            }

            if (safety.Value!.Issues.FirstOrDefault(issue =>
                    issue.Code == DefinitionImportIssueCode.UnsafePayload
                    || issue.IsBlocking) is { } unsafeIssue)
            {
                return Failure<DefinitionBundleExportReceipt>(FromIssue(unsafeIssue));
            }

            await WriteBundleAtomicallyAsync(path, bundle, cancellationToken).ConfigureAwait(false);
            return DefinitionStoreResult<DefinitionBundleExportReceipt>.Success(new(
                path,
                bundle.Definitions.Count,
                bundle.ExportedAt,
                bundle.ReconnectRequiredDatabasePanelCount));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<DefinitionBundleExportReceipt>("The definition export was cancelled.");
        }
        catch (ArgumentException exception)
        {
            return Invalid<DefinitionBundleExportReceipt>(
                $"The selected export path is invalid: {exception.Message}");
        }
        catch (JsonException)
        {
            return Invalid<DefinitionBundleExportReceipt>(
                "The definition export could not be encoded safely.");
        }
        catch (NotSupportedException)
        {
            return Invalid<DefinitionBundleExportReceipt>(
                "The definition export contains an unsupported value.");
        }
        catch (Exception exception) when (IsFileBoundaryFailure(exception))
        {
            return FileFailure<DefinitionBundleExportReceipt>(
                "The selected definition export file could not be written.");
        }
    }

    public async ValueTask<DefinitionStoreResult<DefinitionBundleImportPlan>>
        PreflightImportAsync(
            DefinitionImportMode mode,
            CancellationToken cancellationToken)
    {
        try
        {
            var selectedPath = await _pathPicker.PickImportPathAsync(cancellationToken)
                .ConfigureAwait(false);
            if (selectedPath is null)
            {
                return Cancelled<DefinitionBundleImportPlan>("The definition import was cancelled.");
            }

            var path = NormalizeSelectedPath(selectedPath);
            var bundle = await ReadBundleAsync(path, cancellationToken).ConfigureAwait(false);
            ValidateDockLayoutPayloads(bundle);
            var preflight = await _bundleStore.PreflightImportAsync(
                    bundle,
                    mode,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!preflight.IsSuccess)
            {
                return Failure<DefinitionBundleImportPlan>(preflight.Error!);
            }

            return DefinitionStoreResult<DefinitionBundleImportPlan>.Success(
                new DefinitionBundleImportPlan(path, preflight.Value!));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<DefinitionBundleImportPlan>("The definition import was cancelled.");
        }
        catch (ArgumentException exception)
        {
            return Invalid<DefinitionBundleImportPlan>(
                $"The selected import path is invalid: {exception.Message}");
        }
        catch (JsonException)
        {
            return Invalid<DefinitionBundleImportPlan>(
                "The selected file is not a valid GhostShell definition bundle.");
        }
        catch (NotSupportedException)
        {
            return Invalid<DefinitionBundleImportPlan>(
                "The selected definition bundle uses an unsupported JSON shape.");
        }
        catch (InvalidDataException)
        {
            return Invalid<DefinitionBundleImportPlan>(
                "The selected definition bundle contains an oversized or invalid Dock layout.");
        }
        catch (Exception exception) when (IsFileBoundaryFailure(exception))
        {
            return FileFailure<DefinitionBundleImportPlan>(
                "The selected definition bundle could not be read.");
        }
    }

    /// <summary>
    /// Applies the exact payload represented by <paramref name="plan"/>. Call only after the user
    /// has reviewed its issues and explicitly confirmed the selected conflict mode.
    /// </summary>
    public async ValueTask<DefinitionStoreResult<DefinitionBundleImportReceipt>>
        ConfirmAndApplyImportAsync(
            DefinitionBundleImportPlan plan,
            CancellationToken cancellationToken,
            DefinitionImportExecutionApproval? executionApproval = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.RequiresExecutionReview && executionApproval?.AppliesTo(plan.Preflight) != true)
        {
            return Invalid<DefinitionBundleImportReceipt>(
                "Review and explicitly approve executable content and connection authority for this exact import plan.");
        }
        if (!plan.CanApply)
        {
            var blockingIssue = plan.Issues.First(issue => issue.IsBlocking);
            return Failure<DefinitionBundleImportReceipt>(FromIssue(blockingIssue));
        }

        var affectedWorkspaces = plan.Preflight.Bundle.Definitions
            .Where(document => document.Kind == WorkspaceDefinition.Kind)
            .Select(document => new DefinitionKey(document.Kind, document.Id))
            .Distinct()
            .ToArray();
        using var workspaceLease = affectedWorkspaces.Length == 0
            ? null
            : _workspaceDefinitionOccupancy.TryReserveColdConfigurationEdits(affectedWorkspaces);
        if (affectedWorkspaces.Length > 0 && workspaceLease is null)
        {
            return Failure<DefinitionBundleImportReceipt>(new DefinitionStoreError(
                DefinitionStoreErrorCode.RevisionConflict,
                "Close every affected workspace before importing its definition."));
        }

        DefinitionStoreResult<DefinitionImportResult> committed;
        try
        {
            committed = await _bundleStore.CommitImportAsync(plan.Preflight, cancellationToken, executionApproval)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<DefinitionBundleImportReceipt>(
                "The definition import was cancelled before it committed.");
        }

        if (!committed.IsSuccess)
        {
            return Failure<DefinitionBundleImportReceipt>(committed.Error!);
        }

        DefinitionStoreError? reloadError;
        try
        {
            // The commit is already durable. Caller cancellation must not leave the catalog and
            // workspace launch state disagreeing with the database.
            var reloaded = await _importRefresh.ReloadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            reloadError = reloaded.Error;
        }
        catch (OperationCanceledException)
        {
            reloadError = new DefinitionStoreError(
                DefinitionStoreErrorCode.Cancelled,
                "Definitions were imported, but refreshing the catalog was cancelled internally.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reloadError = new DefinitionStoreError(
                DefinitionStoreErrorCode.StorageFailure,
                "Definitions were imported, but the catalog refresh failed unexpectedly.");
        }

        var workspacesRemainUnavailable = reloadError is not null
            && affectedWorkspaces.Length > 0;
        if (reloadError is null)
        {
            _workspaceDefinitionOccupancy.MarkCatalogReconciled();
        }
        else if (workspacesRemainUnavailable)
        {
            _workspaceDefinitionOccupancy.RetainWorkspaceDefinitionsUntilCatalogReconciled(
                affectedWorkspaces);
            reloadError = reloadError with
            {
                Message = $"{reloadError.Message} Imported workspaces remain unavailable until "
                    + "a catalog refresh succeeds or GhostShell restarts.",
            };
        }

        return DefinitionStoreResult<DefinitionBundleImportReceipt>.Success(new(
            committed.Value!.Inserted,
            committed.Value.Replaced,
            reloadError,
            workspacesRemainUnavailable));
    }

    private static async Task WriteBundleAtomicallyAsync(
        string path,
        PortableDefinitionBundle bundle,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The path has no parent directory.", nameof(path));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            }))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        bundle,
                        DefinitionBundleJsonContext.Default.PortableDefinitionBundle,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task<PortableDefinitionBundle> ReadBundleAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        if (stream.Length is <= 0 or > MaximumImportBytes)
        {
            throw new JsonException(
                $"A definition bundle must contain between 1 and {MaximumImportBytes} bytes.");
        }

        return await JsonSerializer.DeserializeAsync(
                stream,
                DefinitionBundleJsonContext.Default.PortableDefinitionBundle,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException("The definition bundle document is empty.");
    }

    private static void ValidateDockLayoutPayloads(PortableDefinitionBundle bundle)
    {
        if (bundle.Definitions is null)
        {
            // The authoritative store reports the malformed bundle shape.
            return;
        }

        foreach (var definition in bundle.Definitions)
        {
            if (definition is null
                || definition.Kind != DefinitionKind.Layout
                || string.IsNullOrWhiteSpace(definition.PayloadJson))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(definition.PayloadJson);
            }
            catch (JsonException)
            {
                // The authoritative store preflight owns general definition
                // syntax diagnostics. This boundary only rejects a readable
                // Dock field before that deeper deserialization begins.
                continue;
            }

            using var parsedDocument = document;
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(
                        property.Name,
                        "dockLayoutJson",
                        StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind is JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException(
                        "A Dock layout payload must be encoded as a JSON string.");
                }

                var payload = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    _ = DockLayoutPayloadCodec.Decode(payload);
                }

                break;
            }
        }
    }

    private static string NormalizeSelectedPath(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        if (selectedPath.Contains('\0'))
        {
            throw new ArgumentException("The path contains a null character.", nameof(selectedPath));
        }

        var path = Path.GetFullPath(selectedPath.Trim());
        if (string.IsNullOrWhiteSpace(Path.GetFileName(path)))
        {
            throw new ArgumentException("The path must identify a file.", nameof(selectedPath));
        }

        return path;
    }

    private static DefinitionStoreError FromIssue(DefinitionImportIssue issue) => new(
        issue.Code switch
        {
            DefinitionImportIssueCode.UnsafePayload => DefinitionStoreErrorCode.UnsafePayload,
            DefinitionImportIssueCode.UnsupportedKind => DefinitionStoreErrorCode.UnsupportedKind,
            DefinitionImportIssueCode.UnsupportedSchema => DefinitionStoreErrorCode.UnsupportedSchema,
            DefinitionImportIssueCode.MissingDependency => DefinitionStoreErrorCode.DependencyConflict,
            DefinitionImportIssueCode.ExistingIdentity => DefinitionStoreErrorCode.RevisionConflict,
            _ => DefinitionStoreErrorCode.InvalidDefinition,
        },
        issue.Message);

    private static bool IsFileBoundaryFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or System.Security.SecurityException;

    private static DefinitionStoreResult<T> Failure<T>(DefinitionStoreError error) =>
        DefinitionStoreResult<T>.Failure(error);

    private static DefinitionStoreResult<T> Cancelled<T>(string message) =>
        Failure<T>(new DefinitionStoreError(DefinitionStoreErrorCode.Cancelled, message));

    private static DefinitionStoreResult<T> Invalid<T>(string message) =>
        Failure<T>(new DefinitionStoreError(DefinitionStoreErrorCode.InvalidDefinition, message));

    private static DefinitionStoreResult<T> FileFailure<T>(string message) =>
        Failure<T>(new DefinitionStoreError(DefinitionStoreErrorCode.StorageUnavailable, message));
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(PortableDefinitionBundle))]
[JsonSerializable(typeof(WorkspaceDefinition))]
[JsonSerializable(typeof(LayoutDefinition))]
internal sealed partial class DefinitionBundleJsonContext : JsonSerializerContext;
