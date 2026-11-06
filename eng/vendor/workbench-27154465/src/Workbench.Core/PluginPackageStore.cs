using System.Collections.Concurrent;
using System.Text.Json;

namespace Workbench.Core;

/// <summary>
/// Imports disabled packages and journals restart-only maintenance. The caller must own
/// the host user-root lease before ApplyPendingAsync and call it before any discovery.
/// Code rollback never restores business data, settings, credentials or external stores.
/// </summary>
public sealed class PluginPackageStore
{
    private readonly string _pluginsRoot;
    private readonly string _userRoot;
    private readonly string[] _bundledRoots;
    private readonly Version _hostVersion;
    private readonly PackageJournal _journal;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentQueue<PluginDiagnostic> _diagnostics = new();
    private PendingPackageOperation[] _pending = [];
    private PackageHistorySnapshot[] _history = [];

    public PluginPackageStore(PluginManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pluginsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.PluginsRoot));
        _userRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.UserRoot));
        _bundledRoots = options.AdditionalDiscoveryRoots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Where(root => !root.Equals(_pluginsRoot, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _hostVersion = options.HostVersion;
        FileSystemGuard.RejectLinks(_pluginsRoot);
        FileSystemGuard.RejectLinks(_userRoot);
        _journal = new(_pluginsRoot);
        _gate = LoadedPackageRegistry.Gate(_pluginsRoot);
    }

    public IReadOnlyList<PendingPackageOperation> PendingOperations => Array.AsReadOnly(Volatile.Read(ref _pending));
    public IReadOnlyList<PackageHistorySnapshot> History => Array.AsReadOnly(Volatile.Read(ref _history));
    public IReadOnlyList<PluginDiagnostic> Diagnostics => Array.AsReadOnly(_diagnostics.ToArray());

    /// <summary>Folder or root-manifest ZIP; never replaces an existing ID or grants trust.</summary>
    public async Task<PluginOperationResult> InstallAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? stage = null;
        try
        {
            stage = FileSystemGuard.ContainedPath(_journal.Root, "imports/" + Guid.NewGuid().ToString("N"));
            var package = await PackageArchive.ImportAsync(path, stage, _hostVersion, cancellationToken);
            var id = package.Manifest!.Id;
            if (File.Exists(_journal.Root) || File.Exists(_journal.PendingRoot))
                return new(false, "Package maintenance ownership cannot be read safely.");
            var pendingPath = _journal.PendingPath(id);
            if ((await FindPackagesAsync(id, cancellationToken)).Count != 0 || File.Exists(pendingPath) || Directory.Exists(pendingPath))
                return new(false, "The plugin ID is already installed or has pending maintenance.");
            var target = FileSystemGuard.ContainedPath(_pluginsRoot, id);
            if (Directory.Exists(target) || File.Exists(target) || LoadedPackageRegistry.WasLoaded(target))
                return new(false, "The installation path exists or was loaded in this process.");
            await VerifyAsync(stage, id, package.Fingerprint!, cancellationToken);
            // Install also revokes a historical approval for an ID that was
            // removed manually, including reinstallation of identical bytes.
            await RevokeAsync(id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemGuard.RejectLinks(target);
            LoadedPackageRegistry.MoveUnloadedDirectory(stage, target);
            stage = null;
            return new(true, $"Installed '{id}'. It is disabled and requires explicit trust.");
        }
        catch (Exception error) when (IsStorageError(error)) { return Failure("Package import failed."); }
        finally
        {
            try { if (stage is not null) DeletePrivateStage(stage); }
            finally { _gate.Release(); }
        }
    }

    public Task<PluginOperationResult> StageUpdateAsync(string pluginId, string path, CancellationToken cancellationToken = default)
        => StageAsync(pluginId, PackageOperationKind.Update, path, null, cancellationToken);

    /// <summary>historyId is the opaque HistoryId from a refreshed history snapshot.</summary>
    public Task<PluginOperationResult> StageRollbackAsync(string pluginId, string historyId, CancellationToken cancellationToken = default)
        => StageAsync(pluginId, PackageOperationKind.Rollback, null, historyId, cancellationToken);

    public Task<PluginOperationResult> StageRemoveAsync(string pluginId, CancellationToken cancellationToken = default)
        => StageAsync(pluginId, PackageOperationKind.Remove, null, null, cancellationToken);

    private async Task<PluginOperationResult> StageAsync(string id, PackageOperationKind kind, string? source, string? historyId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string? stage = null;
        try
        {
            if (!PackageValidator.IsSafePluginId(id)) return new(false, "A safe namespaced plugin ID is required.");
            var pendingPath = _journal.PendingPath(id);
            if (File.Exists(pendingPath) || Directory.Exists(pendingPath)) return new(false, "This plugin already has pending maintenance.");
            var packages = await FindPackagesAsync(id, token);
            if (packages.Any(package => !IsManaged(package.Path))) return new(false, "Bundled packages cannot be maintained in place.");
            if (packages.Count > 1 || packages.Count == 0 && kind != PackageOperationKind.Rollback)
                return new(false, "A single installed user-managed package is required.");
            var current = packages.SingleOrDefault();
            if (current is not null) PackageArchive.RequireValid(current);
            if (kind == PackageOperationKind.Rollback)
            {
                if (!Guid.TryParseExact(historyId, "N", out _)) return new(false, "A valid code history ID is required.");
                var historyPath = FileSystemGuard.ContainedPath(_journal.HistoryRoot, $"{id}/{historyId}/record.json");
                var historical = await PackageJournal.ReadAsync(historyPath, token);
                if (historical.PluginId != id || historical.OperationId != historyId || historical.OldFingerprint is null ||
                    historical.Phase != PackageOperationPhase.Committed)
                    return new(false, "This history entry has no committed code snapshot.");
                source = _journal.CodePath(historical);
                await VerifyAsync(source, id, historical.OldFingerprint, token);
            }
            var record = new PackageJournalRecord(1, id, Guid.NewGuid().ToString("N"), kind, PackageOperationPhase.Prepared,
                current is null ? id : Path.GetFileName(current.Path), current?.Fingerprint, null,
                current?.Manifest!.Version, null, null, DateTimeOffset.UtcNow);
            if (source is not null)
            {
                stage = _journal.StagePath(record);
                var imported = await PackageArchive.ImportAsync(source, stage, _hostVersion, token);
                if (imported.Manifest!.Id != id) return new(false, "The new package ID does not match the installed package.");
                if (kind == PackageOperationKind.Update && Version.Parse(imported.Manifest.Version) <= Version.Parse(current!.Manifest!.Version))
                    return new(false, "An update must have a strictly higher version.");
                record = record with { NewFingerprint = imported.Fingerprint, NewVersion = imported.Manifest.Version };
                await VerifyAsync(stage, id, record.NewFingerprint!, token);
            }
            await _journal.WriteAsync(record, token);
            stage = null;
            await RefreshCoreAsync(token);
            return new(true, $"{kind} staged for '{id}'. Restart is required.");
        }
        catch (Exception error) when (IsStorageError(error)) { return Failure("Package maintenance could not be staged."); }
        finally
        {
            try { if (stage is not null) DeletePrivateStage(stage); }
            finally { _gate.Release(); }
        }
    }

    public async Task<PluginOperationResult> CancelPendingAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!PackageValidator.IsSafePluginId(pluginId)) return new(false, "A safe namespaced plugin ID is required.");
            var record = await ReadPendingAsync(_journal.PendingPath(pluginId), cancellationToken);
            if (record.Phase != PackageOperationPhase.Prepared || Directory.Exists(_journal.CodePath(record)))
                return new(false, "Maintenance has begun; startup recovery must finish it.");
            if (record.OldFingerprint is not null) await VerifyAsync(_journal.TargetPath(record), pluginId, record.OldFingerprint, cancellationToken);
            else if (Directory.Exists(_journal.TargetPath(record)) || File.Exists(_journal.TargetPath(record)))
                return new(false, "The installation path has changed.");
            DeletePrivateStage(_journal.StagePath(record));
            File.Delete(_journal.PendingPath(pluginId));
            await RefreshCoreAsync(cancellationToken);
            return new(true, "Pending maintenance canceled. Active code was not changed.");
        }
        catch (Exception error) when (IsStorageError(error)) { return Failure("Pending maintenance could not be canceled."); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Processes journal files in ordinal plugin-ID order, returning one result per file.
    /// Repeated calls before discovery are idempotent; after discovery or any package load,
    /// affected paths are protected until process exit. Failures remain local and diagnostic.
    /// </summary>
    public async Task<IReadOnlyList<PluginOperationResult>> ApplyPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<PluginOperationResult>();
            foreach (var path in PendingFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var record = await ReadPendingAsync(path, cancellationToken);
                    if (LoadedPackageRegistry.WasLoaded(_journal.TargetPath(record)) ||
                        LoadedPackageRegistry.WasLoaded(_journal.CodePath(record)) || LoadedPackageRegistry.WasLoaded(_journal.StagePath(record)))
                        results.Add(Failure("A package path was loaded in this process; exit and restart before applying maintenance."));
                    else if (LoadedPackageRegistry.HasDiscovered(_pluginsRoot))
                        results.Add(Failure("Package maintenance is startup-only; discovery has already begun in this process."));
                    else if ((await FindPackagesAsync(record.PluginId, cancellationToken)).Any(package => !IsManaged(package.Path)))
                        results.Add(Failure("A bundled package now owns this plugin ID; maintenance was refused."));
                    else results.Add(await ApplyAsync(record, cancellationToken));
                }
                catch (Exception error) when (IsStorageError(error)) { results.Add(Failure("Package recovery stopped safely; its journal and files were preserved.")); }
            }
            await RefreshCoreAsync(cancellationToken);
            return Array.AsReadOnly(results.ToArray());
        }
        catch (Exception error) when (IsStorageError(error)) { return [Failure("The package journal directory cannot be read safely.")]; }
        finally { _gate.Release(); }
    }

    private async Task<PluginOperationResult> ApplyAsync(PackageJournalRecord record, CancellationToken token)
    {
        var target = _journal.TargetPath(record);
        var stage = _journal.StagePath(record);
        var old = _journal.CodePath(record);
        if (record.Phase is PackageOperationPhase.RecoveryPrepared or PackageOperationPhase.Quarantined)
            return await RecoverOldAsync(record, token);
        if (record.NewFingerprint is not null && record.Phase is (PackageOperationPhase.NewMoved or PackageOperationPhase.Committed))
        {
            string? placedFingerprint;
            try { placedFingerprint = await ExistingFingerprintAsync(target, record.PluginId, token); }
            catch (Exception error) when (IsStorageError(error)) { return await RecoverOldAsync(record, token); }
            if (placedFingerprint != record.NewFingerprint) return await RecoverOldAsync(record, token);
        }
        if (record.Phase is PackageOperationPhase.Committed or PackageOperationPhase.Recovered)
        {
            if (record.Phase == PackageOperationPhase.Recovered)
                await VerifyAsync(target, record.PluginId, record.OldFingerprint!, token);
            else if (record.NewFingerprint is { } committed) await VerifyAsync(target, record.PluginId, committed, token);
            else if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Unexpected current code after removal.");
            await RevokeAsync(record.PluginId, token);
            await CompleteAsync(record, token);
            return new(record.Phase == PackageOperationPhase.Committed, "Package recovery replay completed.");
        }

        // A move can finish before its next journal write. The verified physical
        // layout, never the phase alone, decides which action can be replayed.
        var targetFingerprint = await ExistingFingerprintAsync(target, record.PluginId, token);
        var oldFingerprint = await ExistingFingerprintAsync(old, record.PluginId, token);
        if (targetFingerprint is not null && targetFingerprint != record.OldFingerprint && targetFingerprint != record.NewFingerprint)
            throw new IOException("Current code changed unexpectedly.");
        if (oldFingerprint is not null && oldFingerprint != record.OldFingerprint)
            throw new IOException("Code history changed unexpectedly.");
        if (targetFingerprint == record.OldFingerprint && targetFingerprint is not null && oldFingerprint is not null)
            throw new IOException("Both current code and history claim the old package.");

        if (record.NewFingerprint is not null && targetFingerprint != record.NewFingerprint)
        {
            try { await VerifyAsync(stage, record.PluginId, record.NewFingerprint, token); }
            catch (Exception error) when (IsStorageError(error))
            {
                if (targetFingerprint is null && oldFingerprint == record.OldFingerprint && oldFingerprint is not null)
                {
                    return await RecoverOldAsync(record, token);
                }
                throw;
            }
        }
        if (record.Phase == PackageOperationPhase.Prepared)
        {
            if (targetFingerprint != record.OldFingerprint || oldFingerprint is not null)
                throw new IOException("Prepared maintenance no longer matches current code.");
            var backup = await PackageDataBackup.CreateAsync(_userRoot, record.PluginId, _journal.DataPath(record), token);
            record = record with { Phase = PackageOperationPhase.BackedUp, DataFingerprint = backup };
            await _journal.WriteAsync(record, token);
        }
        else if (await PackageDataBackup.FingerprintAsync(_journal.DataPath(record), token) != record.DataFingerprint)
            throw new IOException("Host state backup changed unexpectedly.");

        if (targetFingerprint == record.OldFingerprint && targetFingerprint is not null)
        {
            // Revalidate immediately before the only destructive (same-volume)
            // rename; rollback/remove also keep the old code as history.
            await VerifyAsync(target, record.PluginId, record.OldFingerprint!, token);
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            FileSystemGuard.RejectLinks(old);
            LoadedPackageRegistry.MoveUnloadedDirectory(target, old);
            targetFingerprint = null;
            oldFingerprint = record.OldFingerprint;
        }
        if (record.OldFingerprint is not null && oldFingerprint != record.OldFingerprint)
            throw new IOException("Verified old code is unavailable.");
        if (record.Phase == PackageOperationPhase.BackedUp)
        {
            record = record with { Phase = PackageOperationPhase.OldMoved };
            await _journal.WriteAsync(record, token);
        }
        if (record.NewFingerprint is not null && targetFingerprint is null)
        {
            await VerifyAsync(stage, record.PluginId, record.NewFingerprint, token);
            FileSystemGuard.RejectLinks(target);
            LoadedPackageRegistry.MoveUnloadedDirectory(stage, target);
        }
        if (record.NewFingerprint is { } expected) await VerifyAsync(target, record.PluginId, expected, token);
        else if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Removal target unexpectedly exists.");
        record = record with { Phase = PackageOperationPhase.NewMoved };
        await _journal.WriteAsync(record, token);
        await RevokeAsync(record.PluginId, token);
        record = record with { Phase = PackageOperationPhase.Committed };
        await _journal.WriteAsync(record, token);
        await CompleteAsync(record, token);
        return new(true, $"{record.Kind} applied to '{record.PluginId}'. Code requires explicit trust; current data was not restored.");
    }

    private async Task<PluginOperationResult> RecoverOldAsync(PackageJournalRecord record, CancellationToken token)
    {
        if (record.OldFingerprint is null) throw new IOException("There is no verified old code to recover.");
        var target = _journal.TargetPath(record);
        var old = _journal.CodePath(record);
        var quarantine = _journal.QuarantinePath(record);
        FileSystemGuard.RejectLinks(quarantine);
        // Replaying after restore's move but before its phase write.
        if (record.Phase is (PackageOperationPhase.RecoveryPrepared or PackageOperationPhase.Quarantined) &&
            !Directory.Exists(old))
        {
            await VerifyAsync(target, record.PluginId, record.OldFingerprint, token);
        }
        else
        {
            await VerifyAsync(old, record.PluginId, record.OldFingerprint, token);
            FileSystemGuard.RejectLinks(target);
            if (File.Exists(target)) throw new IOException("The current package is not a directory.");
            if (Directory.Exists(target))
            {
                if (record.Phase is not (PackageOperationPhase.NewMoved or PackageOperationPhase.Committed or PackageOperationPhase.RecoveryPrepared))
                    throw new IOException("The journal does not confirm ownership of placed new code.");
                // A damaged manifest is recoverable; unsafe descendant paths or
                // a preexisting quarantine cannot be guessed at or overwritten.
                _ = PackageValidator.Inventory(target);
                if (Directory.Exists(quarantine) || File.Exists(quarantine)) throw new IOException("Quarantine is already occupied.");
                record = record with { Phase = PackageOperationPhase.RecoveryPrepared };
                await _journal.WriteAsync(record, token);
                Directory.CreateDirectory(_journal.OperationRoot(record));
                LoadedPackageRegistry.MoveUnloadedDirectory(target, quarantine);
            }
            else if (record.Phase is not (PackageOperationPhase.RecoveryPrepared or PackageOperationPhase.Quarantined))
            {
                record = record with { Phase = PackageOperationPhase.RecoveryPrepared };
                await _journal.WriteAsync(record, token);
            }
            record = record with { Phase = PackageOperationPhase.Quarantined };
            await _journal.WriteAsync(record, token);
            await VerifyAsync(old, record.PluginId, record.OldFingerprint, token);
            FileSystemGuard.RejectLinks(target);
            LoadedPackageRegistry.MoveUnloadedDirectory(old, target);
        }
        record = record with { Phase = PackageOperationPhase.Recovered };
        await _journal.WriteAsync(record, token);
        await RevokeAsync(record.PluginId, token);
        await CompleteAsync(record, token);
        return Failure("Damaged new code was preserved; verified old code was recovered and requires explicit trust. Current data was not restored.");
    }

    private async Task CompleteAsync(PackageJournalRecord record, CancellationToken token)
    {
        await _journal.WriteHistoryAsync(record, token);
        FileSystemGuard.RejectLinks(_journal.PendingPath(record.PluginId));
        File.Delete(_journal.PendingPath(record.PluginId));
    }

    private async Task RevokeAsync(string id, CancellationToken token)
    {
        var runtime = new PluginRuntimeState(_userRoot);
        await runtime.ClearAsync(id, removeConsent: true, _diagnostics.Enqueue, token);
        var preferences = new PluginStateStore(_userRoot);
        await preferences.SaveAsync(id, enabled: false, fingerprint: null, token);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await RefreshCoreAsync(cancellationToken); }
        catch (Exception error) when (IsStorageError(error)) { Failure("Package maintenance metadata cannot be read safely."); }
        finally { _gate.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken token)
    {
        var pending = new List<PendingPackageOperation>();
        foreach (var path in PendingFiles())
        {
            try
            {
                var record = await ReadPendingAsync(path, token);
                pending.Add(new(record.PluginId, record.OperationId, record.Kind, record.Phase, _journal.TargetPath(record),
                    record.NewFingerprint is null ? null : _journal.StagePath(record), record.OldVersion, record.NewVersion));
            }
            catch (Exception error) when (IsStorageError(error)) { Failure("A pending package journal is invalid; its files were preserved."); }
        }
        Volatile.Write(ref _pending, pending.ToArray());
        var history = new List<PackageHistorySnapshot>();
        FileSystemGuard.RejectLinks(_journal.HistoryRoot);
        if (Directory.Exists(_journal.HistoryRoot))
            foreach (var idPath in Directory.EnumerateDirectories(_journal.HistoryRoot).Order(StringComparer.Ordinal))
            {
                if (!PackageValidator.IsSafePluginId(Path.GetFileName(idPath))) { Failure("An unknown history directory was preserved."); continue; }
                try
                {
                    FileSystemGuard.RejectLinks(idPath);
                    foreach (var operation in Directory.EnumerateDirectories(idPath).Order(StringComparer.Ordinal))
                    {
                        try
                        {
                            var record = await PackageJournal.ReadAsync(FileSystemGuard.ContainedPath(operation, "record.json"), token);
                            if (record.PluginId != Path.GetFileName(idPath) || record.OperationId != Path.GetFileName(operation)) throw new JsonException();
                            if (record.Phase != PackageOperationPhase.Committed || record.OldFingerprint is null) continue;
                            await VerifyAsync(_journal.CodePath(record), record.PluginId, record.OldFingerprint, token);
                            history.Add(new(record.PluginId, record.OperationId, record.Kind, record.OldVersion!, record.OldFingerprint,
                                _journal.CodePath(record), record.DataFingerprint is null ? null : _journal.DataPath(record), record.CreatedAt));
                        }
                        catch (Exception error) when (IsStorageError(error)) { Failure("A code history entry is incomplete or damaged; its files were preserved."); }
                    }
                }
                catch (Exception error) when (IsStorageError(error)) { Failure("A code history directory cannot be read safely."); }
            }
        Volatile.Write(ref _history, history.OrderBy(entry => entry.PluginId, StringComparer.Ordinal)
            .ThenByDescending(entry => entry.CreatedAt).ThenBy(entry => entry.HistoryId, StringComparer.Ordinal).ToArray());
    }

    private string[] PendingFiles()
    {
        if (File.Exists(_journal.Root) || File.Exists(_journal.PendingRoot))
            throw new IOException("Package maintenance metadata is not a directory.");
        FileSystemGuard.RejectLinks(_journal.PendingRoot);
        return Directory.Exists(_journal.PendingRoot)
            ? Directory.EnumerateFileSystemEntries(_journal.PendingRoot).Order(StringComparer.Ordinal).ToArray() : [];
    }

    private async Task<PackageJournalRecord> ReadPendingAsync(string path, CancellationToken token)
    {
        var record = await PackageJournal.ReadAsync(path, token);
        if (Path.GetFileName(path) != record.PluginId + ".json") throw new JsonException("Journal filename and ID differ.");
        return record;
    }

    private async Task<List<ValidatedPackage>> FindPackagesAsync(string id, CancellationToken token)
    {
        var packages = new List<ValidatedPackage>();
        foreach (var root in new[] { _pluginsRoot }.Concat(_bundledRoots))
        {
            FileSystemGuard.RejectLinks(root);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                if (root == _pluginsRoot && Path.GetFileName(path) == PackageJournal.DirectoryName) continue;
                var package = await PackageValidator.ValidateAsync(path, _hostVersion, token);
                if (package.Manifest?.Id == id) packages.Add(package);
            }
        }
        return packages;
    }

    private bool IsManaged(string path) => Path.GetDirectoryName(path)!.Equals(_pluginsRoot, StringComparison.OrdinalIgnoreCase);
    private async Task VerifyAsync(string path, string id, string fingerprint, CancellationToken token)
    {
        var package = await PackageValidator.ValidateAsync(path, _hostVersion, token);
        if (!package.IsValid || package.Manifest!.Id != id || package.Fingerprint != fingerprint)
            throw new IOException("A package copy no longer matches its recorded fingerprint.");
    }

    private async Task<string?> ExistingFingerprintAsync(string path, string id, CancellationToken token)
    {
        FileSystemGuard.RejectLinks(path);
        if (File.Exists(path)) throw new IOException("A package directory was replaced by a file.");
        if (!Directory.Exists(path)) return null;
        var package = await PackageValidator.ValidateAsync(path, _hostVersion, token);
        if (!package.IsValid || package.Manifest!.Id != id) throw new IOException("An existing package cannot be verified.");
        return package.Fingerprint;
    }

    private PluginOperationResult Failure(string message)
    {
        var diagnostic = new PluginDiagnostic(PluginDiagnosticCode.PackageMaintenanceFailed, message);
        if (!_diagnostics.Contains(diagnostic)) _diagnostics.Enqueue(diagnostic);
        return new(false, message);
    }

    private void DeletePrivateStage(string stage)
    {
        try
        {
            if (!Directory.Exists(stage)) return;
            // Verify every descendant before recursive deletion. Corrupt/linked
            // stages are preserved, never traversed during cleanup.
            _ = PackageValidator.Inventory(stage);
            Directory.Delete(stage, recursive: true);
        }
        catch (Exception error) when (IsStorageError(error)) { Failure("A private import stage could not be removed safely; it was preserved."); }
    }

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException;
}
